﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Xml;

using Microsoft.Samples.Debugging.MdbgEngine;
using Microsoft.Samples.Debugging.CorDebug;
using Microsoft.Samples.Debugging.CorDebug.NativeApi;

namespace DotNetDbgp.ClientDebugger {
	public class Client {
		private const bool SHOW_MESSAGES = false;
		private readonly int _pid;
		private readonly int _port;
		private readonly Object _mdbgProcessLock = new Object();
		private bool _detaching = false;

		private String _steppingCommand;
		private String _steppingTransId;
		private WaitHandle _stepWait;
		private String _messageBuffer = "";

		private int _maxChildren = 100;
		private int _maxData = 3000;
		private int _maxDepth = 1;

		private Socket _socket;
		private MDbgProcess _mdbgProcess;

		public Client(int pid, int port) {
			_pid = pid;
			_port = port;
		}

		public void Start() {
			var ip = IPAddress.Loopback;
			var ipEndPoint = new IPEndPoint(ip, _port);

			_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			_socket.Connect(ipEndPoint);

			//new Thread(() => {
				this.Run();
			//}).Start();
		}

		public void Run() {
			try {
				var engine = new MDbgEngine();
				_mdbgProcess = engine.Attach(_pid, VersionPolicy.GetDefaultAttachVersion(_pid));
				_mdbgProcess.AsyncStop().WaitOne();

				Action<IRuntimeModule> processModule = (IRuntimeModule module) => {
					var managedModule = module as ManagedModule;
					if (managedModule != null && managedModule.SymReader != null) {
						if (!managedModule.CorModule.JITCompilerFlags.HasFlag(CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION)) {
							return;
						}

						managedModule.CorModule.SetJmcStatus(true, new int[0]);
					}
				};
				Action<IRuntime> processRuntime = (IRuntime runtime) => {
					runtime.ModuleLoaded += (Object sender, RuntimeModuleEventArgs args) => {
						processModule(args.Module);
					};
					var managedRuntime = (ManagedRuntime)runtime;

					IList<MDbgModule> modules = null;
					while(true) {
						try {
							modules = _mdbgProcess.Modules.ToList();
							break;
						} catch (InvalidOperationException) {}
					}
					foreach(var module in modules) {
						foreach(var managedModule in managedRuntime.Modules.LookupAll(module.FriendlyName, true)) {
							processModule(managedModule);
						}
					}
				};
				_mdbgProcess.Runtimes.RuntimeAdded += (Object sender, RuntimeLoadEventArgs runTimeArgs) => {
					processRuntime(runTimeArgs.Runtime);
				};
				foreach(var runtime in _mdbgProcess.Runtimes) {
					processRuntime(runtime);
				}

				var sourcePosition = !_mdbgProcess.Threads.HaveActive || !_mdbgProcess.Threads.Active.HaveCurrentFrame ? null : _mdbgProcess.Threads.Active.CurrentSourcePosition;

				_socket.Send(Encoding.UTF8.GetBytes(this.GenerateOutputMessage(this.InitXml(sourcePosition != null ? sourcePosition.Path : null))));

				Console.CancelKeyPress += delegate {
					Console.Write("Exiting...");
					this.Detach();
					System.Environment.Exit(-1);
				};

				var socketBuffer = new byte[4096];
				var receiveToken = _socket.BeginReceive(socketBuffer, 0, socketBuffer.Length, SocketFlags.None, null, null);
				while(true) {
					var waitArray = _stepWait != null ? new WaitHandle[] { receiveToken.AsyncWaitHandle, _stepWait } : new WaitHandle[] { receiveToken.AsyncWaitHandle };
					var waitIndex = System.Threading.WaitHandle.WaitAny(waitArray);

					if (waitIndex == 0) {
						var readLength = _socket.EndReceive(receiveToken);
						if (readLength > 0) {
							_messageBuffer += Encoding.UTF8.GetString(socketBuffer, 0, readLength);
							this.HandleReadySocket();
						} else if (readLength == 0) {
							_socket.Close();
							_socket = null;
							break;
						} else {
							throw new Exception("Receive failed");
						}

						receiveToken = _socket.BeginReceive(socketBuffer, 0, socketBuffer.Length, SocketFlags.None, null, null);
					} else if (waitIndex == 1) {
						this.HandleBreak();
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
			if (_socket != null) {
				_socket.Close();
			}
		}

		private void HandleReadySocket() {
			while(_messageBuffer.Contains("\0")) {
				var message = _messageBuffer.Substring(0, _messageBuffer.IndexOf('\0'));
#pragma warning disable 162
				if (SHOW_MESSAGES) { Console.WriteLine("Message: "+(message.Length > 1000 ? message.Substring(0, 1000) : message)); }
#pragma warning restore 162

				_messageBuffer = _messageBuffer.Substring(message.Length+1);
				var parsedMessage = this.ParseInputMessage(message);

				Func<String,String,String> getParamOrDefault = (String key, String defaultVal) => {
					string val;
					parsedMessage.Item2.TryGetValue("-"+key, out val);
					val = val ?? defaultVal;
					return val;
				};

				var transId = getParamOrDefault("i", "");

				var command = parsedMessage.Item1;

				String outputMessage;
				switch(command) {
					case "status":
						outputMessage = this.ContinuationXml(parsedMessage.Item1, transId);
						break;
					case "feature_get": {
							var name = getParamOrDefault("n", "");
							outputMessage = this.FeatureGetXml(transId, name);
						}
						break;
					case "feature_set": {
							var name = getParamOrDefault("n", "");
							var newValue = getParamOrDefault("v", "");
							outputMessage = this.FeatureSetXml(transId, name, newValue);
						}
						break;
					default:
						if (_mdbgProcess.IsAlive) {
							switch(command) {
								case "detach":
									this.Detach();
									outputMessage = this.ContinuationXml(parsedMessage.Item1, transId);
									break;
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
								case "break":
									if (_stepWait == null || command == "break") {
										_steppingCommand = command;
										_steppingTransId = transId;
										outputMessage = null;
										this.Step();
									} else {
										outputMessage = this.ErrorXml(parsedMessage.Item1, transId, 5, "Requested stepping while already stepping");
									}
									break;
								case "stop":
									_mdbgProcess.Kill().WaitOne();
									outputMessage = this.ContinuationXml(parsedMessage.Item1, transId);
									break;
								case "stack_get": {
										var depthStr = getParamOrDefault("c", "");
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
								case "eval":
								case "expr":
								case "exec":
									outputMessage = this.EvalXml(parsedMessage.Item1, transId, parsedMessage.Item3);
									break;
								default:
									outputMessage = this.ErrorXml(parsedMessage.Item1, transId, 4, "Test");
									break;
							}
						} else {
							outputMessage = this.ContinuationXml(parsedMessage.Item1, transId);
						}
						break;
				}

				if (outputMessage != null) {
					var realMessage = this.GenerateOutputMessage(outputMessage);
					_socket.Send(Encoding.UTF8.GetBytes(realMessage));
				}
			}
		}

		private void HandleBreak() {
			lock(_mdbgProcessLock) {
				var validStop = _mdbgProcess.StopReason is AsyncStopStopReason
				|| _mdbgProcess.StopReason is BreakpointHitStopReason
				|| _mdbgProcess.StopReason is StepCompleteStopReason
				|| _mdbgProcess.StopReason is ProcessExitedStopReason
				|| _detaching;
				if (validStop) {
					if (_mdbgProcess.StopReason is ProcessExitedStopReason) {
						Console.Write("Attached process ended");
						this.Detach();
					} else {
						Console.WriteLine("Breaking");
					}
					var outputMessage = this.ContinuationXml(_steppingCommand, _steppingTransId);
					var realMessage = this.GenerateOutputMessage(outputMessage);
					_socket.Send(Encoding.UTF8.GetBytes(realMessage));
					_stepWait = null;
					_steppingCommand = null;
					_steppingTransId = null;
				} else {
					var errorStop = _mdbgProcess.StopReason as ErrorStopReason;
					if (errorStop != null) {
						// HACK - Work around for MDBG bug - it errors if unknown threads exit
						if (errorStop.ExceptionThrown.Message == "The given key was not present in the dictionary."
							&& errorStop.ExceptionThrown.StackTrace.Contains("Microsoft.Samples.Debugging.MdbgEngine.ManagedRuntime.ExitThreadEventHandler")) {
							//Console.WriteLine(String.Format("Continuing - invalid stop: {0}", "MDBG exit thread bug"));
							_mdbgProcess.AsyncStop().WaitOne(); // Force valid stop state
							if (_mdbgProcess.StopReason is AsyncStopStopReason) {
								this.Step();
							} else {
								Console.WriteLine(String.Format("Consumed unexpected stop"));
								HandleBreak();
							}
						} else {
							Console.WriteLine(String.Format("Continuing erred: {0}", errorStop.ExceptionThrown));
							throw errorStop.ExceptionThrown;
						}
					} else  {
						Console.WriteLine(String.Format("Continuing - invalid stop: {0}", _mdbgProcess.StopReason));
						this.Step();
					}
				}
			}
		}

		private void Step() {
			lock(_mdbgProcessLock) {
				switch (_steppingCommand) {
					case "break":
						_stepWait = _mdbgProcess.AsyncStop();
						break;
					case "run":
						_stepWait = _mdbgProcess.Go();
						break;
					case "step_into":
						_stepWait = StepImpl(_mdbgProcess, StepperType.In, false);
						break;
					case "step_over":
						_stepWait = StepImpl(_mdbgProcess, StepperType.Over, false);
						break;
					case "step_out":
						_stepWait = StepImpl(_mdbgProcess, StepperType.Out, false);
						break;
					default:
						if (_steppingCommand != null) {
							throw new Exception("Assertion failed: "+_steppingCommand);
						}
						break;
				}
			}
		}

		private static WaitHandle StepImpl(MDbgProcess mdbgProcess, StepperType type, bool nativeStepping) {
			//HACKHACKHACK
			mdbgProcess.GetType().GetMethod("EnsureCanExecute", BindingFlags.NonPublic|BindingFlags.Instance, null, new[] { typeof(String) }, null).Invoke(mdbgProcess, new Object[] { "stepping" });
			try {
				var frameData = mdbgProcess.Threads.Active.BottomFrame.GetPreferedFrameData(((IRuntime)mdbgProcess.Runtimes.NativeRuntime ?? mdbgProcess.Runtimes.ManagedRuntime));
				var stepDesc = frameData.CreateStepperDescriptor(type, nativeStepping);
				var managerStepDesc = stepDesc as ManagedStepperDescriptor;
				if (managerStepDesc != null) managerStepDesc.IsJustMyCode = true;
				stepDesc.Step();
			} catch (Microsoft.Samples.Debugging.MdbgEngine.NoActiveInstanceException) {
				try {
					foreach(var thread in mdbgProcess.Threads) {
						try {
							System.Console.WriteLine(thread.Id);
						} catch { System.Console.WriteLine("None"); }
						try {
								System.Console.WriteLine(thread.BottomFrame.Function.FullName);
						} catch { System.Console.WriteLine("None"); }
					}
				} catch {}
			}
			//HACKHACKHACK
			mdbgProcess.GetType().GetMethod("EnterRunningState", BindingFlags.NonPublic|BindingFlags.Instance, null, new Type[0], null).Invoke(mdbgProcess, new Object[0]);
			return mdbgProcess.StopEvent;
		}

		private String EvalXml(String command, String transId, byte[] data) {
			var input = System.Text.Encoding.UTF8.GetString(data);
			var rawArguments = this.ParseEvalMessage(input);

			try {
				var evalResult = DoEval(rawArguments);

				var resultStr = evalResult.Item1 ? this.ContextGetPropertyXml(evalResult.Item2, _maxDepth, input) : String.Empty;

				return String.Format(
					"<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
					+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"{0}\" transaction_id=\"{1}\" success=\"{2}\">"
					+"	{3}"
					+"</response>",
					command,
					transId,
					evalResult.Item1 ? 1 : 0,
					resultStr
				);
			} catch (Exception e) {
				return this.ErrorXml(command, transId, 206, e.ToString());
			}
		}

		private CorValue[] ParseEvalArguments(IEnumerable<String> arguments, CorEval eval) {
			return arguments.Select(i => {
				bool boolVal;
				int intVal;
				double doubleVal;
				if (int.TryParse(i, out intVal)) {
					return this.MakeVal(intVal, CorElementType.ELEMENT_TYPE_I4, eval);
				} else if (double.TryParse(i, out doubleVal)) {
					return this.MakeVal(doubleVal, CorElementType.ELEMENT_TYPE_R8, eval);
				} else if (bool.TryParse(i, out boolVal)) {
					return this.MakeVal(boolVal, CorElementType.ELEMENT_TYPE_BOOLEAN, eval);
				} else if (i[0] == '\"' && i[i.Length-1] == '\"') {
					return this.MakeStr(i.Substring(1, i.Length - 2), eval);
				} else if (i[0] == '\'' && i[i.Length-1] == '\'') {
					return this.MakeVal(i[1], CorElementType.ELEMENT_TYPE_CHAR, eval);
				} else if (i[0] == '$') {
					return _mdbgProcess.DebuggerVars[i].CorValue;
				} else {
					var variable = _mdbgProcess.ResolveVariable(i, _mdbgProcess.Threads.Active.BottomFrame);
					//Console.WriteLine(String.Format("Argument: {0}", variable.GetStringValue(0)));
					if (variable != null) {
						return variable.CorValue;
					}
				}
				throw new Exception(String.Format("Could not parse value from: {0}", i));
			})
			.ToArray();
		}

		private CorFunction GetFunction(String name) {
			var function = _mdbgProcess.ResolveFunctionNameFromScope(name);
			//Console.WriteLine(String.Format("Function: {0}", function));
			return function == null ? null : function.CorFunction;
		}

		//private CorFunction GetMethod(CorValue thisObj, String name, ManagedThread managedThread) {
		//	var managedValue = new ManagedValue(managedThread.Runtime, thisObj);
		//	ManagedModule managedModule;
		//	var type = _mdbgProcess.ResolveClass(managedValue.TypeName, managedThread.CorThread.AppDomain, out managedModule);
		//	if (type != null && managedModule != null) {
		//		var function =  _mdbgProcess.ResolveFunctionName(managedModule, managedValue.TypeName, name);
		//		return function == null ? null : function.CorFunction;
		//	}
		//	return null;
		//}

		private Type GetType(CorClass corClass, ManagedThread managedThread, out ManagedModule managedModule) {
			if (corClass != null) {
				managedModule = managedThread.Runtime.Modules.Lookup(corClass.Module);
				return managedModule.Importer.GetType(corClass.Token);
			} else {
				managedModule = null;
				return null;
			}
		}

		private bool IsAssignableFrom(Type targetType, Type sourceType, ManagedModule managedModule) {
			var sourceTypeToken = sourceType.MetadataToken;
			var targetTypeToken = targetType.MetadataToken;
			while(true) {
				if (targetTypeToken == sourceTypeToken) {
					return true;
				}
				var interfaceTokens = managedModule.Importer.EnumInterfaceImpls(sourceTypeToken);
				if(interfaceTokens.Any(i => i == targetTypeToken)) {
					return true;
				}
				String typeName; TypeAttributes typeAttributes; int extends;
				managedModule.Importer.GetTypeDefProps(sourceTypeToken, out typeName, out typeAttributes, out extends);
				if (typeName == "System.Object") { break; }
				sourceTypeToken = extends;
			}
			return false;
		}

		private CorFunction GetMethod(CorValue thisObj, String name, CorValue[] arguments, ManagedThread managedThread) {
			ManagedModule dummy;
			var argumentTypes = arguments.Select(i => this.GetType(i.ExactType.Class, managedThread, out dummy)).ToArray();
			ManagedModule managedModule;
			var corClass = thisObj.ExactType.Class;

			var type = this.GetType(corClass, managedThread, out managedModule);
			while(type != null && managedModule != null) {
				var methods = type.GetMethods().Where(i => i != null && i.Name == name).ToArray();
				foreach(var method in methods) {
					if (method != null && method.Name == name) {
						var parameters = method.GetParameters().ToArray();
						var okay = parameters.Select((i, j) => j).Aggregate(true, (i, j) =>
							i && argumentTypes.Length > j ? IsAssignableFrom(parameters[j].ParameterType, argumentTypes[j], managedModule)
							: parameters[j].IsOptional
						);
						if (okay) {
							var function = managedModule.GetFunction(method.MetadataToken);
							if (function != null) {
								return function.CorFunction;
							}
						}
					}
				}
				String typeName; TypeAttributes typeAttributes; int extends;
				managedModule.Importer.GetTypeDefProps(corClass.Token, out typeName, out typeAttributes, out extends);
				corClass = typeName != "System.Object" ? managedModule.CorModule.GetClassFromToken(extends) : (CorClass)null;
				type = this.GetType(corClass, managedThread, out managedModule);
			}
			return null;
		}

		private Tuple<bool,ManagedValue> DoEval(Tuple<String,IList<String>> rawArguments) {
			var managedThread =  _mdbgProcess.Threads.Active.Get<ManagedThread>();
			try {
				_mdbgProcess.TemporaryDefaultManagedRuntime.CorProcess.SetAllThreadsDebugState(CorDebugThreadState.THREAD_SUSPEND, managedThread.CorThread);
				var eval = managedThread.CorThread.CreateEval();

				var function = this.GetFunction(rawArguments.Item1);
				var arguments = rawArguments.Item2;
				if (function == null) {
					if(arguments.Count() == 0) {
						var result = this.ParseEvalArguments(new[] {rawArguments.Item1}, eval).Single();
						return Tuple.Create(true, new ManagedValue(_mdbgProcess.Threads.Active.Get<ManagedThread>().Runtime, result));
					} else if (arguments.First() == "=") {
						var sourceArguments = arguments.Skip(1);
						var source = this.DoEval(Tuple.Create(sourceArguments.First(), (IList<String>)sourceArguments.Skip(1).ToList())).Item2.CorValue;
						if (rawArguments.Item1.First() == '$') {
							var target = _mdbgProcess.DebuggerVars[rawArguments.Item1];
							target.Value = source;
						} else {
							var target = this.ParseEvalArguments(new[] { rawArguments.Item1 }, eval).First();
							var genericTarget = target as CorGenericValue;
							if (genericTarget != null) {
								genericTarget.SetValue(source.CastToGenericValue().GetValue());
							} else if (target is CorReferenceValue) {
								var refTarget = target as CorReferenceValue;
								refTarget.Value = source.CastToReferenceValue().Value;
							}
						}
						return Tuple.Create(true, new ManagedValue(_mdbgProcess.Threads.Active.Get<ManagedThread>().Runtime, source));
					} else if (arguments.First() == ".") {
						var methodArgs = arguments.Skip(2);
						var thisObj = this.ParseEvalArguments(new[] {rawArguments.Item1}, eval).Single();
						var parsedArguments = this.ParseEvalArguments(methodArgs, eval);
						var method = this.GetMethod(thisObj, arguments.Skip(1).First(), parsedArguments, managedThread);
						if (method != null) {
							var functionArgs = (new List<CorValue> { thisObj });
							functionArgs.AddRange(parsedArguments);
							return this.DoFunctionEval(method, functionArgs.ToArray(), eval, managedThread);
						} else {
							throw new Exception("Could not find method");
						}
					} else {
						throw new Exception("Could not parse eval");
					}
				} else {
					var parsedArguments = this.ParseEvalArguments(arguments, eval);
					return this.DoFunctionEval(function, parsedArguments, eval, managedThread);
				}
			} finally {
				managedThread.Runtime.CorProcess.SetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN, managedThread.CorThread);
			}
		}

		private Tuple<bool,ManagedValue> DoFunctionEval(CorFunction function, CorValue[] arguments, CorEval eval, ManagedThread managedThread) {
			eval.CallFunction(function, arguments);
			while(true) {
				_mdbgProcess.Go().WaitOne();
				if (_mdbgProcess.StopReason is EvalExceptionStopReason || _mdbgProcess.StopReason is ProcessExitedStopReason) {
					var errorStop = _mdbgProcess.StopReason as EvalExceptionStopReason;
					if (errorStop != null) {
						throw new Exception(InternalUtil.PrintCorType(_mdbgProcess, errorStop.Eval.Result.ExactType));
					} else {
						return Tuple.Create(false, (ManagedValue)null);
					}
				}
				if (_mdbgProcess.StopReason is EvalCompleteStopReason) {
					break;
				}
			}
			return Tuple.Create(true, new ManagedValue(managedThread.Runtime, eval.Result));
		}

		private CorValue MakeStr(String val, CorEval eval) {
			eval.NewString(val);
			_mdbgProcess.Go().WaitOne();
			if (!(_mdbgProcess.StopReason is EvalCompleteStopReason)) throw new Exception();
			return eval.Result;
		}

		private CorValue MakeVal(object val, CorElementType type, CorEval eval) {
			var corVal = eval.CreateValue(type, null).CastToGenericValue();
			corVal.SetValue(val);
			return corVal;
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
				command, transId, errorCode, String.Empty, this.EscapeXml(errorMessage));
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
				command, transId, _mdbgProcess.IsAlive ? _mdbgProcess.IsRunning ? "running" : "break" : "stopped"
			);
		}

		private String StackGetXml(String transId, int? depth) {
			var activeThread = _mdbgProcess.Threads.HaveActive ? _mdbgProcess.Threads.Active : null;

			var framesString = String.Empty;
			var currentDepth = depth == null ? 0 : depth.Value;
			var frames = activeThread != null ? depth == null ? activeThread.Frames
			                                                  : activeThread.Frames.Skip(depth.Value-1).Take(1)
			           : new MDbgFrame[] { null };
			foreach(var frame in frames) {
				var line = String.Empty;
				var path = String.Empty;
				var where = String.Empty;
				if (frame != null) {
					var source = frame == null ? null : frame.SourcePosition;
					var preferedFrameData = frame.GetPreferedFrameData();
					var function = !(preferedFrameData is ManagedFrameBase) ? null : ((ManagedFrameBase)preferedFrameData).Function;
					where = function != null ? function.FullName : frame.ToString();
					path = source != null ? source.Path : String.Format("dbgp:{0}", function != null ? function.FullName : (String)null);
					line = (source == null ? null : source.Line.ToString()) ?? "";
				}

				framesString += String.Format(
					"<stack level=\"{0}\" type=\"{3}\" filename=\"{1}\" lineno=\"{2}\" where=\"{4}\" cmdbegin=\"\" cmdend=\"\"/>",
					currentDepth,
					this.EscapeXml(path),
					line,
					path.StartsWith("dbgp:") ? "eval" : "file",
					this.EscapeXml(where)
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

				MDbgBreakpoint breakpoint;
				lock(_mdbgProcessLock) {
					_mdbgProcess.AsyncStop().WaitOne();
					breakpoint = _mdbgProcess.Breakpoints.CreateBreakpoint(file, line, true);
					if (!(_mdbgProcess.StopReason is AsyncStopStopReason)) {
						Console.WriteLine(String.Format("Consumed unexpected stop"));
					}
					this.Step();
				}
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
				variablesString.Append(this.ContextGetPropertyXml(var, _maxDepth));
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

			var variablesString = var != null ? this.ContextGetPropertyXml(var, _maxDepth, name) : String.Empty;

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

		private String FeatureGetXml(string transId, string name) {
			String featureValue;
			bool supported = true;
			switch(name) {
				case "language_supports_thread":
					featureValue = "0";
					break;
				case "language_name":
					featureValue = ".NET";
					break;
				case "language_version":
					featureValue = "NYI";
					break;
				case "encoding":
					featureValue = "UTF-8";
					break;
				case "protocol_version":
					featureValue = "1";
					break;
				case "supports_async":
					featureValue = "1";
					break;
				case "data_encoding":
					featureValue = "base64";
					break;
				case "breakpoint_language":
					featureValue = "";
					break;
				case "breakpoint_types":
					featureValue = "line";
					break;
				case "multiple_session":
					featureValue = "0";
					break;
				case "max_children":
					featureValue = _maxChildren.ToString();
					break;
				case "max_data":
					featureValue = _maxData.ToString();
					break;
				case "max_depth":
					featureValue = _maxDepth.ToString();
					break;
				case "detach":
				case "context_names":
				case "context_get":
				case "property_get":
				case "feature_get":
				case "feature_set":
				case "run":
				case "step_into":
				case "step_over":
				case "step_out":
				case "break":
				case "stop":
				case "stack_get":
				case "breakpoint_set":
				case "breakpoint_remove":
				case "eval":
				case "expr":
				case "exec":
				case "status":
					featureValue = "1";
					break;
				default:
					featureValue = String.Empty;
					supported = false;
					break;
			}

			return String.Format(
				 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"feature_get\" supported=\"{1}\" transaction_id=\"{0}\">"
				+"{2}"
				+"</response>",
				transId,
				supported?1:0,
				this.EscapeXml(featureValue)
			);
		}

		private String FeatureSetXml(string transId, string name, string newValue) {
			try {
				switch(name) {
					case "max_children":
						_maxChildren = int.Parse(newValue);
						break;
					case "max_data":
						_maxData = int.Parse(newValue);
						break;
					case "max_depth":
						_maxDepth = int.Parse(newValue);
						break;
					default:
						return this.ErrorXml("feature_set", transId, 3, name+" is an unknown or unsupported feature");
				}
			} catch (FormatException) {
				return this.ErrorXml("feature_set", transId, 3, "["+newValue+"] is invalid for "+name);
			}

			return String.Format(
				 "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
				+"<response xmlns=\"urn:debugger_protocol_v1\" command=\"feature_get\" success=\"{1}\" transaction_id=\"{0}\">"
				+"{2}"
				+"</response>",
				transId,
				1
			);
		}

		private readonly IList<Type> AUTOMATICLY_STRINGIFY = new List<Type> {
			typeof(System.Decimal),
			typeof(System.DateTime),
		};

		private String ContextGetPropertyXml(MDbgValue val, int depth, string fullName = null) {
			if (depth < 0) {
				return String.Empty;
			}
			if (fullName == null) {
				fullName = val.Name;
			}
			var childPropertiesCount = 0;
			var childPropertiesString = new StringBuilder();
			var managedValue = val as ManagedValue;

			if (managedValue.IsArrayType) {
				foreach(var child in managedValue.GetArrayItems().ToList()) {
					if (childPropertiesCount <= _maxChildren) {
						childPropertiesString.Append(this.ContextGetPropertyXml(child, depth-1, fullName+child.Name));
					}
					childPropertiesCount++;
				}
			}
			var automaticlyStringify = AUTOMATICLY_STRINGIFY.Any(i => String.Equals(i.FullName, managedValue.TypeName));
			if (managedValue.IsComplexType && !automaticlyStringify) {
				foreach(var child in managedValue.GetFields()) {
					if (childPropertiesCount <= _maxChildren) {
						childPropertiesString.Append(this.ContextGetPropertyXml(child, depth-1, fullName+"."+child.Name));
					}
					childPropertiesCount++;
				}
			}
			Func<String,String> e = (String i) => this.EscapeXml(i);
			var myValue = e(val.GetStringValue(0, false));
			if (automaticlyStringify) {
				var managedThread =  _mdbgProcess.Threads.Active.Get<ManagedThread>();
				try {
					_mdbgProcess.TemporaryDefaultManagedRuntime.CorProcess.SetAllThreadsDebugState(CorDebugThreadState.THREAD_SUSPEND, managedThread.CorThread);
					var eval = managedThread.CorThread.CreateEval();
					var myValue2 = DoFunctionEval(GetFunction(managedValue.TypeName+".ToString"), new[] { managedValue.CorValue }, eval, managedThread).Item2;
					myValue = e(myValue2.GetStringValue(0, false));
				} finally {
					managedThread.Runtime.CorProcess.SetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN, managedThread.CorThread);
				}
			}
			return String.Format(
				"<property name=\"{0}\" fullname=\"{1}\" type=\"{2}\" classname=\"{2}\" constant=\"0\" children=\"{3}\" size=\"{4}\" encoding=\"none\" numchildren=\"{3}\">{5}{6}</property>",
				e(val.Name), e(fullName), e(val.TypeName), childPropertiesCount, myValue.Length+childPropertiesString.Length, LimitLength(myValue, _maxData), childPropertiesString.ToString()
			);
		}

		private String LimitLength(String val, int maxLength) {
			if (val.Length <= maxLength) {
				return val;
			} else {
				return val.Substring(0, maxLength);
			}
		}

		private Tuple<String,IDictionary<String,String>,byte[]> ParseInputMessage(String message) {
			var arguments = this._ParseInputMessageInner(message, true);

			var parts = arguments.Item2;
			var resultArguments = new Dictionary<String,String>();
			for(var j = 0; j + 1 < parts.Count; j += 2) {
				var key = parts[j];
				var val = parts[j+1];
				resultArguments[key] = val;
			}

			return Tuple.Create(arguments.Item1, (IDictionary<String,String>)resultArguments, arguments.Item3);
		}

		private Tuple<String,IList<String>> ParseEvalMessage(String message) {
			var arguments = this._ParseInputMessageInner(message, false);
			return Tuple.Create(arguments.Item1, arguments.Item2);
		}

		private Tuple<String,IList<String>,byte[]> _ParseInputMessageInner(String message, bool hasBody) {
			var commandSplitter = message.IndexOf(" ");
			if (commandSplitter < 0) commandSplitter = message.Length;
			var command = message.Substring(0, commandSplitter);
			//Console.WriteLine("Command: "+command);

			var inQuotes = false;
			var escape = false;
			var part = String.Empty;
			var parts = new List<String>();
			var i = commandSplitter;
			for(; i < message.Length; i++) {
				var messageChar = message[i];
				if (escape) {
					escape = false;
				} else if (messageChar == '"') {
					inQuotes = !inQuotes;
					continue;
				} else if (messageChar == '\\') {
					escape = true;
					continue;
				} else if (messageChar == ' ' && !inQuotes) {
					if (part.Length != 0) {
						if (part == "--" && hasBody) {
							i++;
							break;
						}
						parts.Add(part);
						//Console.WriteLine("Part: "+part);
						part = String.Empty;
					}
					continue;
				}
				part += messageChar;
				//Console.WriteLine("Part: "+part);
			}

			if (part.Length != 0 && part != "--") {
				parts.Add(part);
				//Console.WriteLine("Part: "+part);
			}

			var bodyStr = message.Substring(i);
			var body = !String.IsNullOrEmpty(bodyStr) ? Convert.FromBase64String(bodyStr) : new byte[0];

			//Console.WriteLine("Body: "+body);

			return Tuple.Create(command, (IList<String>)parts, body);
		}

		private String EscapeXml(String input) {
			return new System.Xml.Linq.XText(input == null ? "<null>" : this.EscapeXmlCharacters(input)).ToString().Replace("\"", "&quot;");
		}

		private String EscapeXmlCharacters(String input) {
			try {
				// throws exception if string contains any invalid characters.
				return XmlConvert.VerifyXmlChars(input);
			} catch {
				var sb = new StringBuilder();

				foreach (var c in input) {
					if (XmlConvert.IsXmlChar(c)) {
						sb.Append(c);
					} else {
						sb.Append(string.Format("[0x{0:X2}]", (short)c));
					}
				}

				return sb.ToString();
			}
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
				}
				_detaching = false;
			}
		}
	}
}
