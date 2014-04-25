﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using Microsoft.Samples.Debugging.MdbgEngine;

namespace DotNetDbgp.ClientDebugger {
	public class Client {
		private const bool SHOW_MESSAGES = false;
		private readonly int _pid;
		private readonly Object _mdbgProcessLock = new Object();
		private bool _detaching = false;

		private Socket _socket;
		private MDbgProcess _mdbgProcess;

		public Client(int pid) {
            _pid = pid;
		}

		public void Start() {
			var ip = IPAddress.Loopback;
			var ipEndPoint = new IPEndPoint(ip, 9000);

			_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			_socket.Connect(ipEndPoint);

			//new Thread(() => {
				this.Run();
			//}).Start();
		}

		public void Run() {
			try {
				var socketBuffer = new byte[4096];
				var messageBuffer = "";

				var engine = new MDbgEngine();
				_mdbgProcess = engine.Attach(_pid, VersionPolicy.GetDefaultAttachVersion(_pid));
				_mdbgProcess.AsyncStop().WaitOne();

				Action<IRuntimeModule> processModule = (IRuntimeModule module) => {
					var managedModule = module as ManagedModule;
					if (managedModule != null && managedModule.SymReader != null) {
						if (!managedModule.CorModule.JITCompilerFlags.HasFlag(Microsoft.Samples.Debugging.CorDebug.CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION)) {
							return;
						}

						managedModule.CorModule.SetJmcStatus(true, new int[0]);
					}
				};
				Action<IRuntime> processRuntime = (IRuntime runtime) => {
					runtime.ModuleLoaded += (Object sender, RuntimeModuleEventArgs args) => {
						processModule(args.Module);
					};
					foreach(var module in _mdbgProcess.Modules.ToList()) {
						var managedModule = _mdbgProcess.TemporaryDefaultManagedRuntime.Modules.Lookup(module.FriendlyName);
						processModule(managedModule);
					}
				};
				foreach(var runtime in _mdbgProcess.Runtimes) {
					processRuntime(runtime);
				}
				_mdbgProcess.Runtimes.RuntimeAdded += (Object sender, RuntimeLoadEventArgs runTimeArgs) => {
					processRuntime(runTimeArgs.Runtime);
				};

				foreach(var module in _mdbgProcess.Modules.ToList()) {
					var managedModule = _mdbgProcess.TemporaryDefaultManagedRuntime.Modules.Lookup(module.FriendlyName);
					processModule(managedModule);
				}
				
				var sourcePosition = !_mdbgProcess.Threads.HaveActive ? null : _mdbgProcess.Threads.Active.CurrentSourcePosition;

				_socket.Send(Encoding.UTF8.GetBytes(this.GenerateOutputMessage(this.InitXml(sourcePosition != null ? sourcePosition.Path : null))));

				Console.CancelKeyPress += delegate {
					Console.Write("Exiting...");
					this.Detach();
					System.Environment.Exit(-1);
				};

				while(true) {
					var readLength = _socket.Receive(socketBuffer);
					if (readLength > 0) {
						messageBuffer += Encoding.UTF8.GetString(socketBuffer, 0, readLength);
					}
					if (readLength < 0) {
						throw new Exception("Receive failed");
					}

					while(messageBuffer.Contains("\0")) {
						var message = messageBuffer.Substring(0, messageBuffer.IndexOf('\0'));
#pragma warning disable 162
						if (SHOW_MESSAGES) { Console.WriteLine("Message: "+(message.Length > 1000 ? message.Substring(0, 1000) : message)); }
#pragma warning restore 162

						messageBuffer = messageBuffer.Substring(message.Length+1);
						var parsedMessage = this.ParseInputMessage(message);

						Func<String,String,String> getParamOrDefault = (String key, String defaultVal) => {
							string val;
							parsedMessage.Item2.TryGetValue("-"+key, out val);
							val = val ?? defaultVal;
							return val;
						};

						var transId = getParamOrDefault("i", "");

						var command = parsedMessage.Item1;

						String outputMessage = null;
						switch(command) {
							case "detach":
								this.Detach();
								return;
							case "context_names":
								outputMessage = this.ContextNamesXml(transId);
								break;
							case "context_get": {
									var contextId = int.Parse(getParamOrDefault("c", "0"));
									var depth = int.Parse(getParamOrDefault("d", "0"));
									outputMessage = this.ContextGetXml(transId, contextId, depth);
								}
								break;
							case "property_get": {
									var contextId = int.Parse(getParamOrDefault("c", "0"));
									var name = getParamOrDefault("n", "");
									var depth = int.Parse(getParamOrDefault("d", "0"));
									outputMessage = this.PropertyGetXml(transId, contextId, name, depth);
								}
								break;
							case "run":
							case "step_into":
							case "step_over":
							case "step_out":
								if (!_mdbgProcess.IsRunning) {
									var validStop = false;
									while(!validStop) {
										WaitHandle wait = null;
										lock(_mdbgProcessLock) {
											switch (command) {
												case "run":
													wait = _mdbgProcess.Go();
													break;
												case "step_into":
													wait = StepImpl(_mdbgProcess, StepperType.In, false);
													break;
												case "step_over":
													wait = StepImpl(_mdbgProcess, StepperType.Over, false);
													break;
												case "step_out":
													wait = StepImpl(_mdbgProcess, StepperType.Out, false);
													break;
												default:
													throw new Exception("Assertion failed");
											}
										}
										wait.WaitOne();
										lock(_mdbgProcessLock) {
											validStop = _mdbgProcess.StopReason is BreakpointHitStopReason
											|| (_mdbgProcess.StopReason is StepCompleteStopReason && _mdbgProcess.Threads.HaveActive && _mdbgProcess.Threads.Active.CurrentSourcePosition != null && _mdbgProcess.Threads.Active.CurrentSourcePosition.Path != null)
											|| _detaching;
											if (!validStop && !(_mdbgProcess.StopReason is StepCompleteStopReason)) {
												var errorStop = _mdbgProcess.StopReason as ErrorStopReason;
												if (errorStop != null) {
													Console.WriteLine(String.Format("Continuing errored: {0}", errorStop.ExceptionThrown));
													throw errorStop.ExceptionThrown;
												} else {
													Console.WriteLine(String.Format("Continuing - invalid stop: {0}", _mdbgProcess.StopReason));
												}
											}
										}
									}
								}
								outputMessage = this.ContinuationXml(parsedMessage.Item1, transId);
								break;
							case "stop":
								_mdbgProcess.Kill().WaitOne();
								outputMessage = this.ContinuationXml(parsedMessage.Item1, transId);
								return;
							case "stack_get": {
									var depthStr = getParamOrDefault("c", "0");
									var depth = String.IsNullOrWhiteSpace(depthStr) ? (int?)null : (int?)int.Parse(depthStr);
									outputMessage = this.StackGetXml(transId, depth);
								}
								break;
							case "breakpoint_set":
								var type = getParamOrDefault("t", "");
								var file = getParamOrDefault("f", "");
								var line = int.Parse(getParamOrDefault("n", "0"));
								var state = getParamOrDefault("s", "");
								outputMessage = this.BreakpointSetXml(transId, type, file, line, state);
								break;
							case "breakpoint_remove":
								var id = int.Parse(getParamOrDefault("d", "0"));
								outputMessage = this.BreakpointRemoveXml(transId, id);
								break;
							default:
								outputMessage = this.ErrorXml(parsedMessage.Item1, transId, 4, "Test");
								break;
						}

						var realMessage = this.GenerateOutputMessage(outputMessage);
						_socket.Send(Encoding.UTF8.GetBytes(realMessage));
					}
				}
			} catch (Exception e) {
				try {
					this.Detach();
				} catch (Exception e2) {
					Console.Error.WriteLine("DETACH FAILURE:\n"+e2.ToString());
				}
				Console.Error.WriteLine(e.ToString());
			}
		}

		private static WaitHandle StepImpl(MDbgProcess mdbgProcess, StepperType type, bool nativeStepping) {
			//HACKHACKHACK
			mdbgProcess.GetType().GetMethod("EnsureCanExecute", BindingFlags.NonPublic|BindingFlags.Instance, null, new[] { typeof(String) }, null).Invoke(mdbgProcess, new Object[] { "stepping" });
			var frameData = (mdbgProcess.Runtimes.NativeRuntime == null ? mdbgProcess.Threads.Active.BottomFrame.GetPreferedFrameData((IRuntime) mdbgProcess.Runtimes.ManagedRuntime) : mdbgProcess.Threads.Active.BottomFrame.GetPreferedFrameData((IRuntime) mdbgProcess.Runtimes.NativeRuntime));
			var stepDesc = frameData.CreateStepperDescriptor(type, nativeStepping);
			var managerStepDesc = stepDesc as ManagedStepperDescriptor;
			if (managerStepDesc != null) managerStepDesc.IsJustMyCode = true;
			stepDesc.Step();
			//HACKHACKHACK
			mdbgProcess.GetType().GetMethod("EnterRunningState", BindingFlags.NonPublic|BindingFlags.Instance, null, new Type[0], null).Invoke(mdbgProcess, new Object[0]);
			return mdbgProcess.StopEvent;
		}

		private String GenerateOutputMessage(String message) {
			var length = message.Length;
			var result = String.Format("{0}\0{1}\0", length.ToString(), message);
#pragma warning disable 162
			if (SHOW_MESSAGES) { Console.WriteLine("Response: "+(result.Length > 1000 ? result.Substring(0, 1000) : result)); }
#pragma warning restore 162
			return result;
		}

		private String ErrorXml(String command, String transId, int errorCode, String errorMessage) {
			return String.Format(
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"{0}\" transaction_id=\"{1}\">"
				+"	<error code=\"{2}\" apperr=\"{3}\">"
				+"		<message>{4}</message>"
				+"	</error>"
				+"</response>"
				,
				command, transId, errorCode, String.Empty, errorMessage);
		}

		private String InitXml(String path) {
			return String.Format(
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<init xmlns=\"urn:debugger_protocol_v1\" appid=\"DotNetDbgp\" idekey=\"\" session=\"\" thread=\"\" parent=\"\" language=\"C#\" protocol_version=\"1.0\" fileuri=\"{0}\" />",
				path ?? "dbgp:null"
			);
		}

		private String ContextNamesXml(String transId) {
			return String.Format(
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"context_names\" transaction_id=\"{0}\">"
				+"	<context name=\"Both\" id=\"0\"/>"
				+"	<context name=\"Local\" id=\"1\"/>"
				+"	<context name=\"Arguments\" id=\"2\"/>"
				+"</response>",
				transId
			);
		}

		private String ContinuationXml(String command, String transId) {
			return String.Format(
				 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"{0}\" status=\"{2}\" reason=\"ok\" transaction_id=\"{1}\"/>",
				command, transId, _mdbgProcess.IsRunning ? "running" : "break"
			);
		}

		private String StackGetXml(String transId, int? depth) {
			var activeThread = _mdbgProcess.Threads.HaveActive ? _mdbgProcess.Threads.Active : null;

			var framesString = String.Empty;
			var currentDepth = depth == null ? 0 : depth.Value;
			var frames = activeThread != null ? /*depth == null ?*/ activeThread.Frames /*
			                                                  : activeThread.Frames.Skip(depth.Value-1).Take(1) */
			           : new MDbgFrame[] { null };
			foreach(var frame in frames) {
				var path = (frame == null || frame.SourcePosition == null ? null : frame.SourcePosition.Path);
				if (path == null) {
					String functionFullName = null;
					if (frame != null) {
						var preferedFrameData = frame.GetPreferedFrameData();
						var function = !(preferedFrameData is ManagedFrameBase) ? null : ((ManagedFrameBase)preferedFrameData).Function;
						functionFullName = function != null ? function.FullName : null;
					}
					path = String.Format("dbgp:{0}", functionFullName ?? "null");
				}
				var line = (frame == null || frame.SourcePosition == null ? null : frame.SourcePosition.Line.ToString()) ?? "";

				framesString += String.Format(
					"<stack level=\"{0}\" type=\"file\" filename=\"{1}\" lineno=\"{2}\" where=\"\" cmdbegin=\"\" cmdend=\"\"/>",
					currentDepth,
					this.EscapeXml(path),
					line
				);
				currentDepth++;
			}
			
			return String.Format(
				 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"stack_get\" transaction_id=\"{0}\">"
				+"	{1}"
				+"</response>",
				transId,
				framesString
			);
		}

		private String BreakpointSetXml(String transId, String type, String file, int line, String state) {
			if (state == "enabled" && type == "line") {
				if (file.StartsWith("file://")) {
					file = file.Substring(7);
				}
				file = file.Replace('/', '\\');
				Console.WriteLine(String.Format("File: {0}, Line: {1}", file, line));
				var breakpoint = _mdbgProcess.Breakpoints.CreateBreakpoint(file, line, true);
				Console.WriteLine(String.Format("Breakpoint: {0}", breakpoint.ToString()));
				return String.Format(
					 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
					+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"breakpoint_set\" transaction_id=\"{0}\" state=\"{1}\" id=\"{2}\"/>",
					transId, state, breakpoint.Number
				);
			} else {
				throw new NotImplementedException(state+"-"+type);
			}
		}

		private String BreakpointRemoveXml(String transId, int id) {
			var breakpoint = _mdbgProcess.Breakpoints.UserBreakpoints.FirstOrDefault(i => i.Number == id);
			if (breakpoint != null) {
				breakpoint.Delete();
			} else {
				Console.WriteLine("Breakpoint not found");
			}
			//Console.WriteLine(String.Format("File: {0}, Line: {1}, Breakpoint: {2}", file, line, breakpoint.ToString()));
			return String.Format(
					"<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"breakpoint_remove\" transaction_id=\"{0}\" />",
				transId
			);
		}

		private String ContextGetXml(String transId, int contextId, int depth) {
			var variables = new List<MDbgValue>();
			if (_mdbgProcess.Threads.HaveActive) {
				var frame = depth == 0 ? _mdbgProcess.Threads.Active.CurrentFrame : _mdbgProcess.Threads.Active.Frames.ElementAt(depth);
				if (contextId == 0 || contextId == 1) {
					variables.AddRange(frame.GetActiveLocalVariables());
				}
				if (contextId == 0 || contextId == 2) {
					variables.AddRange(frame.GetArguments());
				}
			}

			var variablesString = new StringBuilder();
			foreach(var var in variables) {
				variablesString.Append(this.ContextGetPropertyXml(var, 1));
			}
			return String.Format(
				 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"context_get\" context=\"{1}\" transaction_id=\"{0}\">"
				+"{2}"
				+"</response>",
				transId,
				contextId,
				variablesString.ToString()
			);
		}


		private String PropertyGetXml(string transId, int contextId, string name, int depth) {
			var frame = depth == 0 ? _mdbgProcess.Threads.Active.CurrentFrame : _mdbgProcess.Threads.Active.Frames.Cast<MDbgFrame>().ElementAt(depth);

			var var = _mdbgProcess.ResolveVariable(name, frame);

			var variablesString = var != null ? this.ContextGetPropertyXml(var, 1, name) : String.Empty;

			return String.Format(
				 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"context_get\" context=\"{1}\" transaction_id=\"{0}\">"
				+"{2}"
				+"</response>",
				transId,
				contextId,
				variablesString.ToString()
			);
		}

		private String ContextGetPropertyXml(MDbgValue val, int depth, string fullName = null) {
			if (depth < 0) {
				return String.Empty;
			}
			if (fullName == null) {
				fullName = val.Name;
			}
			var childPropertiesCount = 0;
			var childPropertiesString = new StringBuilder();
			if (depth > 0) {
				var managedValue = val as ManagedValue;
				if (managedValue.IsArrayType) {
					foreach(var child in managedValue.GetArrayItems().ToList()) {
						childPropertiesString.Append(this.ContextGetPropertyXml(child, depth-1, fullName+"["+child.Name+"]"));
						childPropertiesCount++;
					}
				}
				if (managedValue.IsComplexType) {
					foreach(var child in managedValue.GetFields()) {
						childPropertiesString.Append(this.ContextGetPropertyXml(child, depth-1, fullName+"."+child.Name));
						childPropertiesCount++;
					}
				}
			}
			Func<String,String> e = (String i) => this.EscapeXml(i);
			var myValue = e(val.GetStringValue(0, false));
			return String.Format(
				"<property name=\"{0}\" fullname=\"{1}\" type=\"{2}\" classname=\"{2}\" constant=\"0\" children=\"{3}\" size=\"{4}\" encoding=\"none\" numchildren=\"{3}\">{5}{6}</property>",
				e(val.Name), e(fullName), e(val.TypeName), childPropertiesCount, myValue.Length+childPropertiesString.Length, myValue, childPropertiesString.ToString()
			);
		}

		private Tuple<String,IDictionary<String,String>,String> ParseInputMessage(String message) {
			var resultArguments = new Dictionary<String,String>();
			var commandSplitter = message.IndexOf(" ");
			var command = message.Substring(0, commandSplitter);
			//Console.WriteLine("Command: "+command);

			var inQuotes = false;
			var escape = false;
			var part = String.Empty;
			var parts = new List<String>();
			var i = commandSplitter + 1;
			for(; i < message.Length; i++) {
				var messageChar = message[i];
				if (!inQuotes) {
					if (messageChar == ' ') {
						if (part.Length != 0) {
							if (part == "--") {
								i++;
								break;
							}
							parts.Add(part);
							//Console.WriteLine("Part: "+part);
							part = String.Empty;
						}
						continue;
					} else if (messageChar == '"') {
						inQuotes = true;
						continue;
					}
				} else if (escape) {
					escape = false;
				} else if (messageChar == '"') {
					inQuotes = false;
					continue;
				} else if (messageChar == '\\') {
					escape = true;
					continue;
				}
				part += messageChar;
				//Console.WriteLine("Part: "+part);
			}

			if (part.Length != 0 && part != "--") {
				parts.Add(part);
				//Console.WriteLine("Part: "+part);
			}

			var body = message.Substring(i);
			//Console.WriteLine("Body: "+body);

			for(var j = 0; j + 1 < parts.Count; j += 2) {
				var key = parts[j];
				var val = parts[j+1];
				resultArguments[key] = val;
			}

			return Tuple.Create(command, (IDictionary<String,String>)resultArguments, body);
		}

		private String EscapeXml(String input) {
			return new System.Xml.Linq.XText(input).ToString();
		}

		public void Detach() {
			lock(_mdbgProcessLock) {
				_detaching = true;
				try {
					if (_mdbgProcess.IsAlive && _mdbgProcess.IsRunning) {
						_mdbgProcess.AsyncStop().WaitOne();
					}
					_mdbgProcess.Breakpoints.DeleteAll();
				} finally {
					_mdbgProcess.Detach().WaitOne();
					_socket.Close();
				}
				_detaching = false;
			}
		}
	}
}
