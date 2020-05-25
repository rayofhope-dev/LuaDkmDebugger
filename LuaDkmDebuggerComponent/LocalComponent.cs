﻿using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.CustomRuntimes;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Microsoft.VisualStudio.Debugger.Native;
using Microsoft.VisualStudio.Debugger.Symbols;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace LuaDkmDebuggerComponent
{
    // TODO: move to a separate file
    internal class Kernel32
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, UIntPtr dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);
    }

    internal class LuaDebugConfiguration
    {
        public List<string> ScriptPaths = new List<string>();
    }

    internal class LuaLocalProcessData : DkmDataItem
    {
        public DkmCustomRuntimeInstance runtimeInstance = null;
        public DkmCustomModuleInstance moduleInstance = null;

        public ulong scratchMemory = 0;

        public bool workingDirectoryRequested = false;
        public string workingDirectory = null;

        public LuaDebugConfiguration configuration = null;

        public SymbolStore symbolStore = new SymbolStore();

        public DkmNativeModuleInstance moduleWithLoadedLua = null;
        public ulong loadLibraryAddress = 0;

        public bool helperInjectRequested = false;
        public bool helperInjected = false;
        public bool helperInitializationWaitActive = false;
        public bool helperInitializationWaitUsed = false;
        public bool helperInitialized = false;
        public DkmThread helperInitializionSuspensionThread;

        public ulong helperHookFunctionAddress = 0;
        public ulong helperBreakLineAddress = 0;
        public ulong helperStepOverAddress = 0;
        public ulong helperStepIntoAddress = 0;
        public ulong helperStepOutAddress = 0;
        public ulong helperSkipDepthAddress = 0;

        public Guid breakpointLuaInitialization;

        public Guid breakpointLuaThreadCreate;
        public Guid breakpointLuaThreadDestroy;
        public Guid breakpointLuaHelperInitialized;

        public Guid breakpointLuaHelperBreakpointHit;
        public Guid breakpointLuaHelperStepComplete;
        public Guid breakpointLuaHelperStepInto;
        public Guid breakpointLuaHelperStepOut;

        public ulong helperStartAddress = 0;
        public ulong helperEndAddress = 0;

        public ulong executionStartAddress = 0;
        public ulong executionEndAddress = 0;
    }

    internal class LuaStackContextData : DkmDataItem
    {
        // Stack walk data for multiple switches between Lua and C++
        public ulong stateAddress = 0;
        public bool seenLuaFrame = false;
        public int skipFrames = 0; // How many Lua frames to skip
        public int seenFrames = 0; // How many Lua frames we have seen

        public bool hideTopLuaLibraryFrames = false;
    }

    internal class LuaFrameLocalsEnumData : DkmDataItem
    {
        public LuaFrameData frameData;
        public LuaFunctionCallInfoData callInfo;
        public LuaFunctionData function;
    }

    internal class LuaEvaluationDataItem : DkmDataItem
    {
        public ulong address;
        public string type;
        public string fullName;
        public LuaValueDataBase luaValueData;
    }

    internal class LuaResolvedDocumentItem : DkmDataItem
    {
        public SymbolSource source;
    }

    public class LocalComponent : IDkmCallStackFilter, IDkmSymbolQuery, IDkmSymbolCompilerIdQuery, IDkmSymbolDocumentCollectionQuery, IDkmLanguageExpressionEvaluator, IDkmSymbolDocumentSpanQuery, IDkmModuleInstanceLoadNotification, IDkmCustomMessageCallbackReceiver, IDkmLanguageInstructionDecoder
    {
        internal string ExecuteExpression(string expression, DkmInspectionSession inspectionSession, DkmThread thread, DkmStackWalkFrame input, DkmEvaluationFlags flags, bool allowZero, out ulong address)
        {
            var compilerId = new DkmCompilerId(DkmVendorId.Microsoft, DkmLanguageId.Cpp);
            var language = DkmLanguage.Create("C++", compilerId);
            var languageExpression = DkmLanguageExpression.Create(language, DkmEvaluationFlags.None, expression, null);

            var inspectionContext = DkmInspectionContext.Create(inspectionSession, input.RuntimeInstance, thread, 200, flags, DkmFuncEvalFlags.None, 10, language, null);

            var workList = DkmWorkList.Create(null);

            try
            {
                string resultText = null;
                ulong resultAddress = 0;

                inspectionContext.EvaluateExpression(workList, languageExpression, input, res =>
                {
                    if (res.ErrorCode == 0)
                    {
                        var result = res.ResultObject as DkmSuccessEvaluationResult;

                        if (result != null && result.TagValue == DkmEvaluationResult.Tag.SuccessResult && (allowZero || result.Address.Value != 0))
                        {
                            resultText = result.Value;
                            resultAddress = result.Address.Value;
                        }

                        res.ResultObject.Close();
                    }
                });

                workList.Execute();

                address = resultAddress;
                return resultText;
            }
            catch (OperationCanceledException)
            {
                address = 0;
                return null;
            }
        }

        internal ulong? TryEvaluateAddressExpression(string expression, DkmInspectionSession inspectionSession, DkmThread thread, DkmStackWalkFrame input, DkmEvaluationFlags flags)
        {
            if (ExecuteExpression(expression, inspectionSession, thread, input, flags, true, out ulong address) != null)
                return address;

            return null;
        }

        internal long? TryEvaluateNumberExpression(string expression, DkmInspectionSession inspectionSession, DkmThread thread, DkmStackWalkFrame input, DkmEvaluationFlags flags)
        {
            string result = ExecuteExpression(expression, inspectionSession, thread, input, flags, true, out _);

            if (result == null)
                return null;

            if (long.TryParse(result, out long value))
                return value;

            return null;
        }

        internal string TryEvaluateStringExpression(string expression, DkmInspectionSession inspectionSession, DkmThread thread, DkmStackWalkFrame input, DkmEvaluationFlags flags)
        {
            return ExecuteExpression(expression + ",sb", inspectionSession, thread, input, flags, false, out _);
        }

        internal void LoadConfigurationFile(DkmProcess process, LuaLocalProcessData processData)
        {
            // Check if already loaded
            if (processData.configuration != null)
                return;

            bool TryLoad(string path)
            {
                if (File.Exists(path))
                {
                    var serializer = new JavaScriptSerializer();

                    try
                    {
                        processData.configuration = serializer.Deserialize<LuaDebugConfiguration>(File.ReadAllText(path));

                        return processData.configuration != null;
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine("Failed to load configuration: " + e.Message);
                    }
                }

                return false;
            }

            string pathA = $"{Path.GetDirectoryName(process.Path)}\\";

            if (TryLoad(pathA + "lua_dkm_debug.json"))
                return;

            if (processData.workingDirectory == null)
                return;

            if (TryLoad($"{processData.workingDirectory}\\" + "lua_dkm_debug.json"))
                return;
        }

        DkmStackWalkFrame[] IDkmCallStackFilter.FilterNextFrame(DkmStackContext stackContext, DkmStackWalkFrame input)
        {
            // null input frame indicates the end of the call stack
            if (input == null)
                return null;

            if (input.InstructionAddress == null)
                return new DkmStackWalkFrame[1] { input };

            if (input.InstructionAddress.ModuleInstance == null)
                return new DkmStackWalkFrame[1] { input };

            if (input.BasicSymbolInfo == null)
                return new DkmStackWalkFrame[1] { input };

            var stackContextData = DebugHelpers.GetOrCreateDataItem<LuaStackContextData>(stackContext);

            if (input.ModuleInstance != null && input.ModuleInstance.Name == "LuaDebugHelper_x86.dll")
            {
                stackContextData.hideTopLuaLibraryFrames = true;

                return new DkmStackWalkFrame[1] { DkmStackWalkFrame.Create(stackContext.Thread, input.InstructionAddress, input.FrameBase, input.FrameSize, DkmStackWalkFrameFlags.NonuserCode | DkmStackWalkFrameFlags.Hidden, "[Lua Debugger Helper]", input.Registers, input.Annotations) };
            }

            if (input.BasicSymbolInfo.MethodName == "luaV_execute")
            {
                stackContextData.hideTopLuaLibraryFrames = false;

                var process = stackContext.InspectionSession.Process;

                var processData = DebugHelpers.GetOrCreateDataItem<LuaLocalProcessData>(process);

                if (processData.runtimeInstance == null)
                {
                    // Request the RemoteComponent to create the runtime and a module
                    // NOTE: Due to issues with Visual Studio debugger, runtime and module were already created as soon as application launched
                    var message = DkmCustomMessage.Create(process.Connection, process, MessageToRemote.guid, MessageToRemote.createRuntime, null, null);

                    message.SendLower();

                    processData.runtimeInstance = process.GetRuntimeInstances().OfType<DkmCustomRuntimeInstance>().FirstOrDefault(el => el.Id.RuntimeType == Guids.luaRuntimeGuid);

                    if (processData.runtimeInstance == null)
                        return new DkmStackWalkFrame[1] { input };

                    processData.moduleInstance = processData.runtimeInstance.GetModuleInstances().OfType<DkmCustomModuleInstance>().FirstOrDefault(el => el.Module != null && el.Module.CompilerId.VendorId == Guids.luaCompilerGuid);

                    if (processData.moduleInstance == null)
                        return new DkmStackWalkFrame[1] { input };
                }

                if (processData.scratchMemory == 0)
                    processData.scratchMemory = process.AllocateVirtualMemory(0, 4096, 0x3000, 0x04);

                if (processData.scratchMemory == 0)
                    return new DkmStackWalkFrame[1] { input };

                // Find out the current process working directory (Lua script files will be resolved from that location)
                if (processData.workingDirectory == null && !processData.workingDirectoryRequested)
                {
                    processData.workingDirectoryRequested = true;

                    // Jumping through hoops, kernel32.dll should be loaded
                    ulong callAddress = DebugHelpers.FindFunctionAddress(process.GetNativeRuntimeInstance(), "GetCurrentDirectoryA");

                    if (callAddress != 0)
                    {
                        long? length = TryEvaluateNumberExpression($"((int(*)(int, char*))0x{callAddress:x})(4095, (char*){processData.scratchMemory})", stackContext.InspectionSession, stackContext.Thread, input, DkmEvaluationFlags.None);

                        if (length.HasValue && length.Value != 0)
                            processData.workingDirectory = TryEvaluateStringExpression($"(const char*){processData.scratchMemory}", stackContext.InspectionSession, stackContext.Thread, input, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);
                    }
                }

                LoadConfigurationFile(process, processData);

                bool isTopFrame = (input.Flags & DkmStackWalkFrameFlags.TopFrame) != 0;

                List<DkmStackWalkFrame> luaFrames = new List<DkmStackWalkFrame>();

                var luaFrameFlags = input.Flags;

                luaFrameFlags &= ~(DkmStackWalkFrameFlags.NonuserCode | DkmStackWalkFrameFlags.UserStatusNotDetermined);

                if (isTopFrame)
                    luaFrameFlags |= DkmStackWalkFrameFlags.TopFrame;

                ulong? stateAddress = TryEvaluateAddressExpression($"L", stackContext.InspectionSession, stackContext.Thread, input, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);

                // Reset Lua frame skip data if we have switched Lua state
                if (stackContextData.stateAddress != stateAddress.GetValueOrDefault(0))
                {
                    stackContextData.stateAddress = stateAddress.GetValueOrDefault(0);
                    stackContextData.seenLuaFrame = false;
                    stackContextData.skipFrames = 0;
                    stackContextData.seenFrames = 0;
                }

                ulong? registryAddress = TryEvaluateAddressExpression($"&L->l_G->l_registry", stackContext.InspectionSession, stackContext.Thread, input, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);
                long? version = TryEvaluateNumberExpression($"(int)*L->l_G->version", stackContext.InspectionSession, stackContext.Thread, input, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);

                LuaHelpers.luaVersion = (int)version.GetValueOrDefault(501); // Lua 5.1 doesn't have version field

                string GetLuaFunctionName(ulong callInfoAddress)
                {
                    string functionNameType = null;

                    DkmCustomMessage.Create(process.Connection, process, MessageToRemote.guid, MessageToRemote.pauseBreakpoints, null, null).SendLower();

                    // Note that in Lua 5.1 call info address if for current call info as opposed to previous call info in future versions
                    if (LuaHelpers.luaVersion == 501 || LuaHelpers.luaVersion == 502)
                        functionNameType = TryEvaluateStringExpression($"getfuncname(L, ((CallInfo*){callInfoAddress}), (const char**){processData.scratchMemory})", stackContext.InspectionSession, stackContext.Thread, input, DkmEvaluationFlags.None);
                    else
                        functionNameType = TryEvaluateStringExpression($"funcnamefromcode(L, ((CallInfo*){callInfoAddress}), (const char**){processData.scratchMemory})", stackContext.InspectionSession, stackContext.Thread, input, DkmEvaluationFlags.None);

                    DkmCustomMessage.Create(process.Connection, process, MessageToRemote.guid, MessageToRemote.resumeBreakpoints, null, null).SendLower();

                    if (functionNameType != null)
                    {
                        ulong? functionNameAddress = DebugHelpers.ReadPointerVariable(process, processData.scratchMemory);

                        if (functionNameAddress.HasValue && functionNameAddress.Value != 0)
                            return DebugHelpers.ReadStringVariable(process, functionNameAddress.Value, 1024);
                    }

                    return null;
                }

                DkmStackWalkFrame GetLuaFunctionStackWalkFrame(ulong callInfoAddress, LuaFunctionCallInfoData callInfoData, LuaValueDataLuaFunction callLuaFunction, string functionName)
                {
                    var currFunctionData = callLuaFunction.value.ReadFunction(process);

                    Debug.Assert(currFunctionData != null);

                    if (currFunctionData == null)
                        return null;

                    Debug.Assert(callInfoData.savedInstructionPointerAddress >= currFunctionData.codeDataAddress);

                    if (callInfoData.savedInstructionPointerAddress < currFunctionData.codeDataAddress)
                        return null;

                    long currInstructionPointer = ((long)callInfoData.savedInstructionPointerAddress - (long)currFunctionData.codeDataAddress) / 4; // unsigned size instructions

                    // If the call was already made, savedpc will be offset by 1 (return location)
                    int prevInstructionPointer = currInstructionPointer == 0 ? 0 : (int)currInstructionPointer - 1;

                    int currLine = currFunctionData.ReadLineInfoFor(process, prevInstructionPointer);

                    string sourceName = currFunctionData.ReadSource(process);

                    if (sourceName != null)
                    {
                        if (currFunctionData.definitionStartLine == 0)
                            functionName = "main";

                        LuaFunctionData functionData = currFunctionData;

                        processData.symbolStore.Add(process, functionData);

                        string argumentList = "";

                        for (int i = 0; i < functionData.argumentCount; i++)
                        {
                            LuaLocalVariableData argument = new LuaLocalVariableData();

                            argument.ReadFrom(process, functionData.localVariableDataAddress + (ulong)(i * LuaLocalVariableData.StructSize(process)));

                            argumentList += (i == 0 ? "" : ", ") + argument.name;
                        }

                        LuaAddressEntityData entityData = new LuaAddressEntityData
                        {
                            functionAddress = callLuaFunction.value.functionAddress,

                            instructionPointer = prevInstructionPointer,

                            source = sourceName
                        };

                        LuaFrameData frameData = new LuaFrameData
                        {
                            state = stateAddress.Value,

                            registryAddress = registryAddress.GetValueOrDefault(0),
                            version = (int)version.GetValueOrDefault(503),

                            callInfo = callInfoAddress,

                            functionAddress = callLuaFunction.value.functionAddress,
                            functionName = functionName,

                            instructionLine = (int)currLine,
                            instructionPointer = prevInstructionPointer,

                            source = sourceName
                        };

                        var entityDataBytes = entityData.Encode();
                        var frameDataBytes = frameData.Encode();

                        DkmInstructionAddress instructionAddress = DkmCustomInstructionAddress.Create(processData.runtimeInstance, processData.moduleInstance, entityDataBytes, (ulong)prevInstructionPointer, frameDataBytes, null);

                        var description = $"{sourceName} {functionName}({argumentList}) Line {currLine}";

                        return DkmStackWalkFrame.Create(stackContext.Thread, instructionAddress, input.FrameBase, input.FrameSize, luaFrameFlags, description, input.Registers, input.Annotations);
                    }

                    return null;
                }

                if (LuaHelpers.luaVersion == 501)
                {
                    // Read lua_State
                    ulong temp = stateAddress.Value;

                    // CommonHeader
                    DebugHelpers.SkipStructPointer(process, ref temp);
                    DebugHelpers.SkipStructByte(process, ref temp);
                    DebugHelpers.SkipStructByte(process, ref temp);

                    DebugHelpers.SkipStructByte(process, ref temp); // status
                    DebugHelpers.SkipStructPointer(process, ref temp); // top
                    DebugHelpers.SkipStructPointer(process, ref temp); // base
                    DebugHelpers.SkipStructPointer(process, ref temp); // l_G
                    ulong callInfoAddress = DebugHelpers.ReadStructPointer(process, ref temp).GetValueOrDefault(0);
                    ulong savedProgramCounterAddress = DebugHelpers.ReadStructPointer(process, ref temp).GetValueOrDefault(0);
                    DebugHelpers.SkipStructPointer(process, ref temp); // stack_last
                    DebugHelpers.SkipStructPointer(process, ref temp); // stack
                    DebugHelpers.SkipStructPointer(process, ref temp); // end_ci
                    ulong baseCallInfoAddress = DebugHelpers.ReadStructPointer(process, ref temp).GetValueOrDefault(0);

                    ulong currCallInfoAddress = callInfoAddress;

                    while (currCallInfoAddress > baseCallInfoAddress)
                    {
                        LuaFunctionCallInfoData currCallInfoData = new LuaFunctionCallInfoData();

                        currCallInfoData.ReadFrom(process, currCallInfoAddress);
                        currCallInfoData.ReadFunction(process);

                        // Last function call info program counter is saved in lua_State
                        if (currCallInfoAddress == callInfoAddress)
                            currCallInfoData.savedInstructionPointerAddress = savedProgramCounterAddress;

                        if (currCallInfoData.func == null)
                            break;

                        LuaFunctionCallInfoData prevCallInfoData = new LuaFunctionCallInfoData();

                        prevCallInfoData.ReadFrom(process, currCallInfoAddress - (DebugHelpers.Is64Bit(process) ? 40ul : 24ul));
                        prevCallInfoData.ReadFunction(process);

                        if (prevCallInfoData.func == null)
                            break;

                        if (stackContextData.skipFrames != 0)
                        {
                            stackContextData.skipFrames--;

                            currCallInfoAddress = currCallInfoAddress - (DebugHelpers.Is64Bit(process) ? 40ul : 24ul);
                            continue;
                        }

                        if (currCallInfoData.func.baseType != LuaBaseType.Function)
                            break;

                        var currCallLuaFunction = currCallInfoData.func as LuaValueDataLuaFunction;

                        Debug.Assert(currCallLuaFunction != null);

                        if (currCallLuaFunction == null)
                            break;

                        var prevCallLuaFunction = prevCallInfoData.func as LuaValueDataLuaFunction;

                        string currFunctionName = "[name unavailable]";

                        // Can't get function name if calling function is unknown because of a tail call or if call was not from Lua
                        if (currCallLuaFunction.value.isC_5_1 == 0 && currCallInfoData.tailCallCount_5_1 > 0)
                        {
                            currFunctionName = $"[name unavailable - tail call]";
                        }
                        else if (prevCallLuaFunction != null && prevCallLuaFunction.value.isC_5_1 != 0)
                        {
                            currFunctionName = $"[name unavailable - not called from Lua]";
                        }
                        else
                        {
                            string functionName = GetLuaFunctionName(currCallInfoAddress);

                            if (functionName != null)
                                currFunctionName = functionName;
                        }

                        if (currCallLuaFunction.value.isC_5_1 == 0)
                        {
                            stackContextData.seenLuaFrame = true;
                            stackContextData.seenFrames++;

                            var frame = GetLuaFunctionStackWalkFrame(currCallInfoAddress, currCallInfoData, currCallLuaFunction, currFunctionName);

                            if (frame != null)
                            {
                                luaFrames.Add(frame);

                                luaFrameFlags &= ~DkmStackWalkFrameFlags.TopFrame;
                            }
                        }
                        else
                        {
                            if (stackContextData.seenLuaFrame)
                            {
                                stackContextData.seenLuaFrame = false;
                                stackContextData.skipFrames = stackContextData.seenFrames;
                                break;
                            }

                            stackContextData.seenFrames++;

                            luaFrames.Add(DkmStackWalkFrame.Create(stackContext.Thread, input.InstructionAddress, input.FrameBase, input.FrameSize, luaFrameFlags, $"[{currFunctionName} C function]", input.Registers, input.Annotations));

                            luaFrameFlags &= ~DkmStackWalkFrameFlags.TopFrame;
                        }

                        currCallInfoAddress = currCallInfoAddress - (DebugHelpers.Is64Bit(process) ? 40ul : 24ul);
                    }
                }
                else
                {
                    ulong? currCallInfoAddress = TryEvaluateAddressExpression($"L->ci", stackContext.InspectionSession, stackContext.Thread, input, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);

                    while (stateAddress.HasValue && currCallInfoAddress.HasValue && currCallInfoAddress.Value != 0)
                    {
                        LuaFunctionCallInfoData currCallInfoData = new LuaFunctionCallInfoData();

                        currCallInfoData.ReadFrom(process, currCallInfoAddress.Value);
                        currCallInfoData.ReadFunction(process);

                        if (currCallInfoData.func == null)
                            break;

                        if (currCallInfoData.func.baseType == LuaBaseType.Function)
                        {
                            if (stackContextData.skipFrames != 0)
                            {
                                stackContextData.skipFrames--;

                                currCallInfoAddress = currCallInfoData.previousAddress;
                                continue;
                            }

                            // Now we need to know what the previous call info used to call us
                            if (currCallInfoData.previousAddress == 0)
                                break;

                            LuaFunctionCallInfoData prevCallInfoData = new LuaFunctionCallInfoData();

                            prevCallInfoData.ReadFrom(process, currCallInfoData.previousAddress);
                            prevCallInfoData.ReadFunction(process);

                            string currFunctionName = "[name unavailable]";

                            // Can't get function name if previous call status is not 'Lua'
                            if (currCallInfoData.CheckCallStatusFinalizer())
                            {
                                currFunctionName = "__gc";
                            }
                            else if (currCallInfoData.CheckCallStatusTailCall())
                            {
                                currFunctionName = $"[name unavailable - tail call]";
                            }
                            else if (!prevCallInfoData.CheckCallStatusLua())
                            {
                                currFunctionName = $"[name unavailable - not called from Lua]";
                            }
                            else
                            {
                                // Check that it's safe to cast previous call info to a Lua Closure
                                if (prevCallInfoData.func.extendedType != LuaExtendedType.LuaFunction)
                                    break;

                                string functionName = GetLuaFunctionName(currCallInfoData.previousAddress);

                                if (functionName != null)
                                    currFunctionName = functionName;
                            }

                            if (currCallInfoData.func.extendedType == LuaExtendedType.LuaFunction)
                            {
                                var currCallLuaFunction = currCallInfoData.func as LuaValueDataLuaFunction;

                                Debug.Assert(currCallLuaFunction != null);

                                if (currCallLuaFunction == null)
                                    break;

                                stackContextData.seenLuaFrame = true;
                                stackContextData.seenFrames++;

                                var frame = GetLuaFunctionStackWalkFrame(currCallInfoAddress.Value, currCallInfoData, currCallLuaFunction, currFunctionName);

                                if (frame != null)
                                {
                                    luaFrames.Add(frame);

                                    luaFrameFlags &= ~DkmStackWalkFrameFlags.TopFrame;
                                }
                            }
                            else if (currCallInfoData.func.extendedType == LuaExtendedType.ExternalFunction)
                            {
                                if (stackContextData.seenLuaFrame)
                                {
                                    stackContextData.seenLuaFrame = false;
                                    stackContextData.skipFrames = stackContextData.seenFrames;
                                    break;
                                }

                                stackContextData.seenFrames++;

                                luaFrames.Add(DkmStackWalkFrame.Create(stackContext.Thread, input.InstructionAddress, input.FrameBase, input.FrameSize, luaFrameFlags, $"[{currFunctionName} C function]", input.Registers, input.Annotations));

                                luaFrameFlags &= ~DkmStackWalkFrameFlags.TopFrame;
                            }
                            else if (currCallInfoData.func.extendedType == LuaExtendedType.ExternalClosure)
                            {
                                if (stackContextData.seenLuaFrame)
                                {
                                    stackContextData.seenLuaFrame = false;
                                    stackContextData.skipFrames = stackContextData.seenFrames;
                                    break;
                                }

                                stackContextData.seenFrames++;

                                luaFrames.Add(DkmStackWalkFrame.Create(stackContext.Thread, input.InstructionAddress, input.FrameBase, input.FrameSize, luaFrameFlags, $"[{currFunctionName} C closure]", input.Registers, input.Annotations));

                                luaFrameFlags &= ~DkmStackWalkFrameFlags.TopFrame;
                            }

                            currCallInfoAddress = currCallInfoData.previousAddress;
                        }
                        else
                        {
                            currCallInfoAddress = null;
                        }
                    }
                }

                luaFrames.Add(DkmStackWalkFrame.Create(stackContext.Thread, null, input.FrameBase, input.FrameSize, DkmStackWalkFrameFlags.NonuserCode, "[Transition to Lua]", input.Registers, input.Annotations));

                return luaFrames.ToArray();
            }

            // Mark lua functions as non-user code
            if (input.BasicSymbolInfo.MethodName.StartsWith("luaD_") || input.BasicSymbolInfo.MethodName.StartsWith("luaV_") || input.BasicSymbolInfo.MethodName.StartsWith("luaG_") || input.BasicSymbolInfo.MethodName.StartsWith("luaF_") || input.BasicSymbolInfo.MethodName.StartsWith("luaB_") || input.BasicSymbolInfo.MethodName.StartsWith("luaH_") || input.BasicSymbolInfo.MethodName.StartsWith("luaT_"))
            {
                var flags = (input.Flags & ~DkmStackWalkFrameFlags.UserStatusNotDetermined) | DkmStackWalkFrameFlags.NonuserCode;

                if (stackContextData.hideTopLuaLibraryFrames)
                    flags |= DkmStackWalkFrameFlags.Hidden;

                return new DkmStackWalkFrame[1] { DkmStackWalkFrame.Create(stackContext.Thread, input.InstructionAddress, input.FrameBase, input.FrameSize, flags, input.Description, input.Registers, input.Annotations) };
            }

            if (stackContextData.hideTopLuaLibraryFrames && input.BasicSymbolInfo.MethodName == "callhook")
            {
                var flags = (input.Flags & ~DkmStackWalkFrameFlags.UserStatusNotDetermined) | DkmStackWalkFrameFlags.NonuserCode | DkmStackWalkFrameFlags.Hidden;

                return new DkmStackWalkFrame[1] { DkmStackWalkFrame.Create(stackContext.Thread, input.InstructionAddress, input.FrameBase, input.FrameSize, flags, input.Description, input.Registers, input.Annotations) };
            }

            return new DkmStackWalkFrame[1] { input };
        }

        string CheckConfigPaths(string processPath, LuaLocalProcessData processData, string winSourcePath)
        {
            if (processData.configuration != null && processData.configuration.ScriptPaths != null)
            {
                foreach (var path in processData.configuration.ScriptPaths)
                {
                    var finalPath = path.Replace('/', '\\');

                    if (!Path.IsPathRooted(finalPath))
                    {
                        if (processData.workingDirectory != null)
                        {
                            string test = Path.GetFullPath(Path.Combine(processData.workingDirectory, finalPath)) + winSourcePath;

                            if (File.Exists(test))
                                return test;
                        }

                        {
                            string test = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(processPath), finalPath)) + winSourcePath;

                            if (File.Exists(test))
                                return test;
                        }
                    }
                    else
                    {
                        string test = finalPath + winSourcePath;

                        if (File.Exists(test))
                            return test;
                    }
                }
            }

            // Check 'empty' path
            if (processData.workingDirectory != null)
            {
                string test = Path.GetFullPath(Path.Combine(processData.workingDirectory, winSourcePath));

                if (File.Exists(test))
                    return test;
            }

            {
                string test = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(processPath), winSourcePath));

                if (File.Exists(test))
                    return test;
            }

            return null;
        }

        string TryFindSourcePath(string processPath, LuaLocalProcessData processData, string source)
        {
            string filePath;

            if (source.StartsWith("@"))
            {
                string winSourcePath = source.Replace('/', '\\');

                filePath = CheckConfigPaths(processPath, processData, winSourcePath.Substring(1));

                if (filePath == null)
                {
                    if (processData.workingDirectory != null)
                        filePath = $"{processData.workingDirectory}\\{winSourcePath.Substring(1)}";
                    else
                        filePath = winSourcePath.Substring(1);
                }
            }
            else
            {
                string winSourcePath = source.Replace('/', '\\');

                filePath = CheckConfigPaths(processPath, processData, winSourcePath);

                if (filePath == null)
                {
                    // TODO: how can we display internal scripts in the debugger?
                    if (processData.workingDirectory != null)
                        filePath = $"{processData.workingDirectory}\\internal.lua";
                    else
                        filePath = "internal.lua";
                }
            }

            return filePath;
        }

        DkmSourcePosition IDkmSymbolQuery.GetSourcePosition(DkmInstructionSymbol instruction, DkmSourcePositionFlags flags, DkmInspectionSession inspectionSession, out bool startOfLine)
        {
            var process = inspectionSession?.Process;

            if (process == null)
            {
                DkmCustomModuleInstance moduleInstance = instruction.Module.GetModuleInstances().OfType<DkmCustomModuleInstance>().FirstOrDefault(el => el.Module.CompilerId.VendorId == Guids.luaCompilerGuid);

                if (moduleInstance == null)
                    return instruction.GetSourcePosition(flags, inspectionSession, out startOfLine);

                process = moduleInstance.Process;
            }

            var processData = DebugHelpers.GetOrCreateDataItem<LuaLocalProcessData>(process);

            var instructionSymbol = instruction as DkmCustomInstructionSymbol;

            Debug.Assert(instructionSymbol != null);

            var frameData = new LuaFrameData();

            if (frameData.ReadFrom(instructionSymbol.AdditionalData.ToArray()))
            {
                string filePath = TryFindSourcePath(process.Path, processData, frameData.source);

                startOfLine = true;
                return DkmSourcePosition.Create(DkmSourceFileId.Create(filePath, null, null, null), new DkmTextSpan(frameData.instructionLine, frameData.instructionLine, 0, 0));
            }

            return instruction.GetSourcePosition(flags, inspectionSession, out startOfLine);
        }

        object IDkmSymbolQuery.GetSymbolInterface(DkmModule module, Guid interfaceID)
        {
            return module.GetSymbolInterface(interfaceID);
        }

        string EvaluateValueAtLuaValue(DkmProcess process, LuaValueDataBase valueBase, uint radix, out string editableValue, ref DkmEvaluationResultFlags flags, out DkmDataAddress dataAddress, out string type)
        {
            editableValue = null;
            dataAddress = null;
            type = "unknown";

            if (valueBase == null)
                return null;

            if (valueBase as LuaValueDataNil != null)
            {
                type = "nil";

                flags |= DkmEvaluationResultFlags.IsBuiltInType | DkmEvaluationResultFlags.ReadOnly;
                return "nil";
            }

            if (valueBase as LuaValueDataBool != null)
            {
                var value = valueBase as LuaValueDataBool;

                type = "bool";

                if (value.value)
                {
                    flags |= DkmEvaluationResultFlags.IsBuiltInType | DkmEvaluationResultFlags.Boolean | DkmEvaluationResultFlags.BooleanTrue;
                    editableValue = $"{value.value}";
                    return "true";
                }
                else
                {
                    flags |= DkmEvaluationResultFlags.IsBuiltInType | DkmEvaluationResultFlags.Boolean;
                    editableValue = $"{value.value}";
                    return "false";
                }
            }

            if (valueBase as LuaValueDataLightUserData != null)
            {
                var value = valueBase as LuaValueDataLightUserData;

                type = "user_data";

                flags |= DkmEvaluationResultFlags.IsBuiltInType;
                editableValue = $"{value.value}";
                return $"0x{value.value:x}";
            }

            if (valueBase as LuaValueDataNumber != null)
            {
                var value = valueBase as LuaValueDataNumber;

                if (value.extendedType == LuaExtendedType.IntegerNumber)
                {
                    type = "int";

                    flags |= DkmEvaluationResultFlags.IsBuiltInType;
                    editableValue = $"{value.value}";

                    if (radix == 16)
                        return $"0x{value.value:x}";

                    return $"{value.value}";
                }
                else
                {
                    type = "double";

                    flags |= DkmEvaluationResultFlags.IsBuiltInType;
                    editableValue = $"{value.value}";
                    return $"{value.value}";
                }
            }

            if (valueBase as LuaValueDataString != null)
            {
                var value = valueBase as LuaValueDataString;

                type = value.extendedType == LuaExtendedType.ShortString ? "short_string" : "long_string";

                flags |= DkmEvaluationResultFlags.IsBuiltInType | DkmEvaluationResultFlags.RawString | DkmEvaluationResultFlags.ReadOnly;
                dataAddress = DkmDataAddress.Create(process.GetNativeRuntimeInstance(), value.targetAddress, null);
                return $"0x{value.targetAddress:x} \"{value.value}\"";
            }

            if (valueBase as LuaValueDataTable != null)
            {
                var value = valueBase as LuaValueDataTable;

                type = "table";

                value.value.LoadValues(process);
                value.value.LoadMetaTable(process);

                flags |= DkmEvaluationResultFlags.ReadOnly | DkmEvaluationResultFlags.Expandable;

                if (value.value.arrayElements.Count != 0 && value.value.nodeElements.Count != 0)
                    return $"table [{value.value.arrayElements.Count} element(s) and {value.value.nodeElements.Count} key(s)]";

                if (value.value.arrayElements.Count != 0)
                    return $"table [{value.value.arrayElements.Count} element(s)]";

                if (value.value.nodeElements.Count != 0)
                    return $"table [{value.value.nodeElements.Count} key(s)]";

                if (value.value.metaTable == null)
                {
                    flags &= ~DkmEvaluationResultFlags.Expandable;

                    return "table [empty]";
                }

                return "table";
            }

            if (valueBase as LuaValueDataLuaFunction != null)
            {
                var value = valueBase as LuaValueDataLuaFunction;

                type = "lua_function";

                flags |= DkmEvaluationResultFlags.IsBuiltInType | DkmEvaluationResultFlags.ReadOnly;
                return $"0x{value.targetAddress}";
            }

            if (valueBase as LuaValueDataExternalFunction != null)
            {
                var value = valueBase as LuaValueDataExternalFunction;

                type = "c_function";

                flags |= DkmEvaluationResultFlags.IsBuiltInType | DkmEvaluationResultFlags.ReadOnly;
                return $"0x{value.targetAddress}";
            }

            if (valueBase as LuaValueDataExternalClosure != null)
            {
                var value = valueBase as LuaValueDataExternalClosure;

                type = "c_closure";

                flags |= DkmEvaluationResultFlags.IsBuiltInType | DkmEvaluationResultFlags.ReadOnly;
                return $"0x{value.targetAddress}";
            }

            if (valueBase as LuaValueDataUserData != null)
            {
                var value = valueBase as LuaValueDataUserData;

                type = "user_data";

                flags |= DkmEvaluationResultFlags.ReadOnly;
                return $"0x{value.targetAddress}";
            }

            if (valueBase as LuaValueDataThread != null)
            {
                var value = valueBase as LuaValueDataThread;

                type = "thread";

                flags |= DkmEvaluationResultFlags.IsBuiltInType | DkmEvaluationResultFlags.ReadOnly;
                return $"0x{value.targetAddress}";
            }

            return null;
        }

        string EvaluateValueAtAddress(DkmProcess process, ulong address, uint radix, out string editableValue, ref DkmEvaluationResultFlags flags, out DkmDataAddress dataAddress, out string type, out LuaValueDataBase luaValueData)
        {
            editableValue = null;
            dataAddress = null;
            type = "unknown";

            luaValueData = LuaHelpers.ReadValue(process, address);

            if (luaValueData == null)
                return null;

            return EvaluateValueAtLuaValue(process, luaValueData, radix, out editableValue, ref flags, out dataAddress, out type);
        }

        DkmEvaluationResult EvaluateDataAtAddress(DkmInspectionContext inspectionContext, DkmStackWalkFrame stackFrame, string name, string fullName, ulong address, DkmEvaluationResultFlags flags, DkmEvaluationResultAccessType access, DkmEvaluationResultStorageType storage)
        {
            var process = stackFrame.Process;

            if (address == 0)
                return DkmFailedEvaluationResult.Create(inspectionContext, stackFrame, name, fullName, "Null pointer access", DkmEvaluationResultFlags.Invalid, null);

            string value = EvaluateValueAtAddress(process, address, inspectionContext.Radix, out string editableValue, ref flags, out DkmDataAddress dataAddress, out string type, out LuaValueDataBase luaValueData);

            if (value == null)
                return DkmFailedEvaluationResult.Create(inspectionContext, stackFrame, name, fullName, "Failed to read value", DkmEvaluationResultFlags.Invalid, null);

            DkmEvaluationResultCategory category = DkmEvaluationResultCategory.Data;
            DkmEvaluationResultTypeModifierFlags typeModifiers = DkmEvaluationResultTypeModifierFlags.None;

            var dataItem = new LuaEvaluationDataItem
            {
                address = address,
                type = type,
                fullName = fullName,
                luaValueData = luaValueData
            };

            return DkmSuccessEvaluationResult.Create(inspectionContext, stackFrame, name, fullName, flags, value, editableValue, type, category, access, storage, typeModifiers, dataAddress, null, null, dataItem);
        }

        DkmEvaluationResult EvaluateDataAtLuaValue(DkmInspectionContext inspectionContext, DkmStackWalkFrame stackFrame, string name, string fullName, LuaValueDataBase luaValue, DkmEvaluationResultFlags flags, DkmEvaluationResultAccessType access, DkmEvaluationResultStorageType storage)
        {
            var process = stackFrame.Process;

            if (luaValue == null)
                return DkmFailedEvaluationResult.Create(inspectionContext, stackFrame, name, fullName, "Null pointer access", DkmEvaluationResultFlags.Invalid, null);

            string value = EvaluateValueAtLuaValue(process, luaValue, inspectionContext.Radix, out string editableValue, ref flags, out DkmDataAddress dataAddress, out string type);

            if (value == null)
                return DkmFailedEvaluationResult.Create(inspectionContext, stackFrame, name, fullName, "Failed to read value", DkmEvaluationResultFlags.Invalid, null);

            DkmEvaluationResultCategory category = DkmEvaluationResultCategory.Data;
            DkmEvaluationResultTypeModifierFlags typeModifiers = DkmEvaluationResultTypeModifierFlags.None;

            var dataItem = new LuaEvaluationDataItem
            {
                address = luaValue.originalAddress,
                type = type,
                fullName = fullName,
                luaValueData = luaValue
            };

            return DkmSuccessEvaluationResult.Create(inspectionContext, stackFrame, name, fullName, flags, value, editableValue, type, category, access, storage, typeModifiers, dataAddress, null, null, dataItem);
        }

        void IDkmLanguageExpressionEvaluator.EvaluateExpression(DkmInspectionContext inspectionContext, DkmWorkList workList, DkmLanguageExpression expression, DkmStackWalkFrame stackFrame, DkmCompletionRoutine<DkmEvaluateExpressionAsyncResult> completionRoutine)
        {
            var process = stackFrame.Process;

            // Load frame data from instruction
            var instructionAddress = stackFrame.InstructionAddress as DkmCustomInstructionAddress;

            Debug.Assert(instructionAddress != null);

            var frameData = new LuaFrameData();

            if (!frameData.ReadFrom(instructionAddress.AdditionalData.ToArray()))
            {
                completionRoutine(new DkmEvaluateExpressionAsyncResult(DkmFailedEvaluationResult.Create(inspectionContext, stackFrame, expression.Text, expression.Text, "Missing function frame data", DkmEvaluationResultFlags.Invalid, null)));
                return;
            }

            // Load call info data
            LuaFunctionCallInfoData callInfoData = new LuaFunctionCallInfoData();

            callInfoData.ReadFrom(process, frameData.callInfo); // TODO: cache?

            // Load function data
            LuaFunctionData functionData = new LuaFunctionData();

            functionData.ReadFrom(process, frameData.functionAddress); // TODO: cache?

            functionData.ReadLocals(process, frameData.instructionPointer);

            ExpressionEvaluation evaluation = new ExpressionEvaluation(process, functionData, callInfoData.stackBaseAddress);

            var result = evaluation.Evaluate(expression.Text);

            if (result as LuaValueDataError != null)
            {
                var resultAsError = result as LuaValueDataError;

                completionRoutine(new DkmEvaluateExpressionAsyncResult(DkmFailedEvaluationResult.Create(inspectionContext, stackFrame, expression.Text, expression.Text, resultAsError.value, DkmEvaluationResultFlags.Invalid, null)));
                return;
            }

            // If result is an 'l-value' re-evaluate as a Lua value at address
            if (result.originalAddress != 0)
            {
                completionRoutine(new DkmEvaluateExpressionAsyncResult(EvaluateDataAtLuaValue(inspectionContext, stackFrame, expression.Text, expression.Text, result, DkmEvaluationResultFlags.None, DkmEvaluationResultAccessType.None, DkmEvaluationResultStorageType.None)));
                return;
            }

            var resultStr = result.AsSimpleDisplayString(inspectionContext.Radix);
            var type = result.GetLuaType();

            DkmEvaluationResultCategory category = DkmEvaluationResultCategory.Data;
            DkmEvaluationResultAccessType accessType = DkmEvaluationResultAccessType.None;
            DkmEvaluationResultStorageType storageType = DkmEvaluationResultStorageType.None;
            DkmEvaluationResultTypeModifierFlags typeModifiers = DkmEvaluationResultTypeModifierFlags.None;

            DkmDataAddress dataAddress = null;

            if (result as LuaValueDataString != null)
            {
                var resultAsString = result as LuaValueDataString;

                if (resultAsString.targetAddress != 0)
                    dataAddress = DkmDataAddress.Create(process.GetNativeRuntimeInstance(), resultAsString.targetAddress, null);
            }

            if (result as LuaValueDataTable != null)
            {
                var resultAsTable = result as LuaValueDataTable;

                resultAsTable.value.LoadValues(process);
                resultAsTable.value.LoadMetaTable(process);

                if (resultAsTable.value.arrayElements.Count == 0 && resultAsTable.value.nodeElements.Count == 0 && resultAsTable.value.metaTable == null)
                    result.evaluationFlags &= ~DkmEvaluationResultFlags.Expandable;
            }

            var dataItem = new LuaEvaluationDataItem
            {
                address = result.originalAddress,
                type = type,
                fullName = expression.Text,
                luaValueData = result
            };

            completionRoutine(new DkmEvaluateExpressionAsyncResult(DkmSuccessEvaluationResult.Create(inspectionContext, stackFrame, expression.Text, expression.Text, result.evaluationFlags, resultStr, null, type, category, accessType, storageType, typeModifiers, dataAddress, null, null, dataItem)));
        }

        DkmEvaluationResult GetTableChildAtIndex(DkmInspectionContext inspectionContext, DkmStackWalkFrame stackFrame, string fullName, LuaValueDataTable value, int index)
        {
            var process = stackFrame.Process;

            if (index < value.value.arrayElements.Count)
            {
                var element = value.value.arrayElements[index];

                return EvaluateDataAtLuaValue(inspectionContext, stackFrame, $"[{index + 1}]", $"{fullName}[{index + 1}]", element, DkmEvaluationResultFlags.None, DkmEvaluationResultAccessType.None, DkmEvaluationResultStorageType.None);
            }

            index = index - value.value.arrayElements.Count;

            if (index < value.value.nodeElements.Count)
            {
                var node = value.value.nodeElements[index];

                DkmEvaluationResultFlags flags = DkmEvaluationResultFlags.None;
                string name = EvaluateValueAtLuaValue(process, node.key, 10, out _, ref flags, out _, out _);

                var keyString = node.key as LuaValueDataString;

                if (keyString != null)
                    name = keyString.value;

                if (name == null || name.Length == 0)
                    name = "%error-name%";

                // Check if name is an identifier
                bool isIdentifierName = false;

                if (char.IsLetter(name[0]) || name[0] == '_')
                {
                    int pos = 1;

                    while (pos < name.Length && (char.IsLetterOrDigit(name[pos]) || name[pos] == '_'))
                        pos++;

                    isIdentifierName = pos == name.Length;
                }

                if (isIdentifierName)
                    return EvaluateDataAtLuaValue(inspectionContext, stackFrame, name, $"{fullName}.{name}", node.value, DkmEvaluationResultFlags.None, DkmEvaluationResultAccessType.None, DkmEvaluationResultStorageType.None);

                return EvaluateDataAtLuaValue(inspectionContext, stackFrame, $"\"{name}\"", $"{fullName}[\"{name}\"]", node.value, DkmEvaluationResultFlags.None, DkmEvaluationResultAccessType.None, DkmEvaluationResultStorageType.None);
            }

            index = index - value.value.nodeElements.Count;

            if (index == 0)
            {
                var metaTableValue = new LuaValueDataTable
                {
                    baseType = LuaBaseType.Table,
                    extendedType = LuaExtendedType.Table,
                    evaluationFlags = DkmEvaluationResultFlags.ReadOnly,
                    originalAddress = 0, // Not available as TValue
                    value = value.value.metaTable,
                    targetAddress = value.value.metaTableDataAddress
                };

                return EvaluateDataAtLuaValue(inspectionContext, stackFrame, "!metatable", $"{fullName}.!metatable", metaTableValue, DkmEvaluationResultFlags.None, DkmEvaluationResultAccessType.None, DkmEvaluationResultStorageType.None);
            }

            Debug.Assert(false, "Invalid child index");

            return null;
        }

        void IDkmLanguageExpressionEvaluator.GetChildren(DkmEvaluationResult result, DkmWorkList workList, int initialRequestSize, DkmInspectionContext inspectionContext, DkmCompletionRoutine<DkmGetChildrenAsyncResult> completionRoutine)
        {
            var process = result.StackFrame.Process;

            var evalData = result.GetDataItem<LuaEvaluationDataItem>();

            // Shouldn't happen
            if (evalData == null)
            {
                completionRoutine(new DkmGetChildrenAsyncResult(new DkmEvaluationResult[0], DkmEvaluationResultEnumContext.Create(0, result.StackFrame, inspectionContext, null)));
                return;
            }

            if (evalData.luaValueData as LuaValueDataTable != null)
            {
                var value = evalData.luaValueData as LuaValueDataTable;

                value.value.LoadValues(process);
                value.value.LoadMetaTable(process);

                int actualSize = value.value.arrayElements.Count + value.value.nodeElements.Count;

                if (value.value.metaTable != null)
                    actualSize += 1;

                int finalInitialSize = initialRequestSize < actualSize ? initialRequestSize : actualSize;

                DkmEvaluationResult[] initialResults = new DkmEvaluationResult[finalInitialSize];

                for (int i = 0; i < initialResults.Length; i++)
                    initialResults[i] = GetTableChildAtIndex(inspectionContext, result.StackFrame, result.FullName, value, i);

                var enumerator = DkmEvaluationResultEnumContext.Create(actualSize, result.StackFrame, inspectionContext, evalData);

                completionRoutine(new DkmGetChildrenAsyncResult(initialResults, enumerator));
                return;
            }

            // Shouldn't happen
            completionRoutine(new DkmGetChildrenAsyncResult(new DkmEvaluationResult[0], DkmEvaluationResultEnumContext.Create(0, result.StackFrame, inspectionContext, null)));
        }

        void IDkmLanguageExpressionEvaluator.GetFrameArguments(DkmInspectionContext inspectionContext, DkmWorkList workList, DkmStackWalkFrame frame, DkmCompletionRoutine<DkmGetFrameArgumentsAsyncResult> completionRoutine)
        {
            completionRoutine(new DkmGetFrameArgumentsAsyncResult(new DkmEvaluationResult[0]));
        }

        void IDkmLanguageExpressionEvaluator.GetFrameLocals(DkmInspectionContext inspectionContext, DkmWorkList workList, DkmStackWalkFrame stackFrame, DkmCompletionRoutine<DkmGetFrameLocalsAsyncResult> completionRoutine)
        {
            var process = stackFrame.Process;

            // Load frame data from instruction
            var instructionAddress = stackFrame.InstructionAddress as DkmCustomInstructionAddress;

            Debug.Assert(instructionAddress != null);

            var frameData = new LuaFrameData();

            if (!frameData.ReadFrom(instructionAddress.AdditionalData.ToArray()))
            {
                completionRoutine(new DkmGetFrameLocalsAsyncResult(DkmEvaluationResultEnumContext.Create(0, stackFrame, inspectionContext, null)));
                return;
            }

            // Load call info data
            LuaFunctionCallInfoData callInfoData = new LuaFunctionCallInfoData();

            callInfoData.ReadFrom(process, frameData.callInfo); // TODO: cache?

            // Load function data
            LuaFunctionData functionData = new LuaFunctionData();

            functionData.ReadFrom(process, frameData.functionAddress); // TODO: cache?

            var frameLocalsEnumData = new LuaFrameLocalsEnumData
            {
                frameData = frameData,
                callInfo = callInfoData,
                function = functionData
            };

            functionData.ReadLocals(process, frameData.instructionPointer);

            int count = 1 + functionData.activeLocals.Count; // 1 pseudo variable for '[registry]' table

            completionRoutine(new DkmGetFrameLocalsAsyncResult(DkmEvaluationResultEnumContext.Create(functionData.localVariableSize, stackFrame, inspectionContext, frameLocalsEnumData)));
        }

        void IDkmLanguageExpressionEvaluator.GetItems(DkmEvaluationResultEnumContext enumContext, DkmWorkList workList, int startIndex, int count, DkmCompletionRoutine<DkmEvaluationEnumAsyncResult> completionRoutine)
        {
            var process = enumContext.StackFrame.Process;

            var frameLocalsEnumData = enumContext.GetDataItem<LuaFrameLocalsEnumData>();

            if (frameLocalsEnumData != null)
            {
                frameLocalsEnumData.function.ReadLocals(process, frameLocalsEnumData.frameData.instructionPointer);

                // Visual Studio doesn't respect enumeration size for GetFrameLocals, so we need to limit it back
                var actualCount = 1 + frameLocalsEnumData.function.activeLocals.Count;

                int finalCount = actualCount - startIndex;

                finalCount = finalCount < 0 ? 0 : (finalCount < count ? finalCount : count);

                var results = new DkmEvaluationResult[finalCount];

                for (int i = startIndex; i < startIndex + finalCount; i++)
                {
                    if (i == 0)
                    {
                        ulong address = frameLocalsEnumData.frameData.registryAddress;

                        string name = "[registry]";

                        results[i - startIndex] = EvaluateDataAtAddress(enumContext.InspectionContext, enumContext.StackFrame, name, name, address, DkmEvaluationResultFlags.None, DkmEvaluationResultAccessType.None, DkmEvaluationResultStorageType.None);
                    }
                    else
                    {
                        int index = i - 1;

                        // Base stack contains arguments and locals that are live at the current instruction
                        ulong address = frameLocalsEnumData.callInfo.stackBaseAddress + (ulong)index * LuaHelpers.GetValueSize(process);

                        string name = frameLocalsEnumData.function.activeLocals[index].name;

                        results[i - startIndex] = EvaluateDataAtAddress(enumContext.InspectionContext, enumContext.StackFrame, name, name, address, DkmEvaluationResultFlags.None, DkmEvaluationResultAccessType.None, DkmEvaluationResultStorageType.None);
                    }
                }

                completionRoutine(new DkmEvaluationEnumAsyncResult(results));
            }

            var evalData = enumContext.GetDataItem<LuaEvaluationDataItem>();

            // Shouldn't happen
            if (evalData == null)
            {
                completionRoutine(new DkmEvaluationEnumAsyncResult(new DkmEvaluationResult[0]));
                return;
            }

            if (evalData.luaValueData as LuaValueDataTable != null)
            {
                var value = evalData.luaValueData as LuaValueDataTable;

                value.value.LoadValues(process);
                value.value.LoadMetaTable(process);

                var results = new DkmEvaluationResult[count];

                for (int i = startIndex; i < startIndex + count; i++)
                    results[i - startIndex] = GetTableChildAtIndex(enumContext.InspectionContext, enumContext.StackFrame, evalData.fullName, value, i);

                completionRoutine(new DkmEvaluationEnumAsyncResult(results));
                return;
            }

            completionRoutine(new DkmEvaluationEnumAsyncResult(new DkmEvaluationResult[0]));
        }

        string IDkmLanguageExpressionEvaluator.GetUnderlyingString(DkmEvaluationResult result)
        {
            var process = result.StackFrame.Process;

            var success = result as DkmSuccessEvaluationResult;

            if (success == null)
                return "Failed to evaluate";

            if (success.Address.Value == 0)
                return "Null pointer access";

            var target = DebugHelpers.ReadStringVariable(process, success.Address.Value, 32 * 1024);

            if (target != null)
                return target;

            return "Failed to read data";
        }

        void IDkmLanguageExpressionEvaluator.SetValueAsString(DkmEvaluationResult result, string value, int timeout, out string errorText)
        {
            var evalData = result.GetDataItem<LuaEvaluationDataItem>();

            if (evalData == null)
            {
                errorText = "Missing evaluation data";
                return;
            }

            var process = result.StackFrame.Process;
            var address = evalData.luaValueData.originalAddress;

            if (evalData.luaValueData.originalAddress == 0)
            {
                errorText = "Result value doesn't have an address in memory";
                return;
            }

            if (evalData.luaValueData as LuaValueDataBool != null)
            {
                if (value == "true")
                {
                    if (!DebugHelpers.TryWriteVariable(process, address, 1))
                        errorText = "Failed to modify target process memory";
                    else
                        errorText = null;

                    return;
                }
                else if (value == "false")
                {
                    if (!DebugHelpers.TryWriteVariable(process, address, 0))
                        errorText = "Failed to modify target process memory";
                    else
                        errorText = null;

                    return;
                }
                else if (int.TryParse(value, out int intValue))
                {
                    if (!DebugHelpers.TryWriteVariable(process, address, intValue != 0 ? 1 : 0))
                        errorText = "Failed to modify target process memory";
                    else
                        errorText = null;

                    return;
                }

                errorText = "Failed to convert string to bool";
                return;
            }

            if (evalData.luaValueData as LuaValueDataLightUserData != null)
            {
                if (ulong.TryParse(value, out ulong ulongValue))
                {
                    if (DebugHelpers.Is64Bit(process) && !DebugHelpers.TryWriteVariable(process, address, (long)ulongValue))
                        errorText = "Failed to modify target process memory";
                    else if (!DebugHelpers.Is64Bit(process) && !DebugHelpers.TryWriteVariable(process, address, (int)ulongValue))
                        errorText = "Failed to modify target process memory";
                    else
                        errorText = null;

                    return;
                }

                errorText = "Failed to convert string to address";
                return;
            }

            if (evalData.luaValueData as LuaValueDataNumber != null)
            {
                if ((evalData.luaValueData as LuaValueDataNumber).extendedType == LuaExtendedType.IntegerNumber)
                {
                    if (int.TryParse(value, out int intValue))
                    {
                        if (!DebugHelpers.TryWriteVariable(process, address, intValue))
                            errorText = "Failed to modify target process memory";
                        else
                            errorText = null;

                        return;
                    }

                    errorText = "Failed to convert string to int";
                    return;
                }
                else
                {
                    if (double.TryParse(value, out double doubleValue))
                    {
                        if (!DebugHelpers.TryWriteVariable(process, address, doubleValue))
                            errorText = "Failed to modify target process memory";
                        else
                            errorText = null;

                        return;
                    }

                    errorText = "Failed to convert string to double";
                    return;
                }
            }

            errorText = "Missing evaluation data";
        }

        string IDkmLanguageInstructionDecoder.GetMethodName(DkmLanguageInstructionAddress languageInstructionAddress, DkmVariableInfoFlags argumentFlags)
        {
            var process = languageInstructionAddress.Address.Process;

            var processData = DebugHelpers.GetOrCreateDataItem<LuaLocalProcessData>(process);

            var customInstructionAddress = languageInstructionAddress.Address as DkmCustomInstructionAddress;

            if (customInstructionAddress == null)
                return languageInstructionAddress.GetMethodName(argumentFlags);

            var addressEntityData = new LuaAddressEntityData();

            addressEntityData.ReadFrom(customInstructionAddress.EntityId.ToArray());

            var breakpointAdditionalData = new LuaBreakpointAdditionalData();

            if (!breakpointAdditionalData.ReadFrom(customInstructionAddress.AdditionalData.ToArray()))
                return languageInstructionAddress.GetMethodName(argumentFlags);

            var functionData = new LuaFunctionData();

            functionData.ReadFrom(process, addressEntityData.functionAddress);

            string source = functionData.ReadSource(process);

            if (source == null)
                source = "unknown script";

            string argumentList = "";

            for (int i = 0; i < functionData.argumentCount; i++)
            {
                LuaLocalVariableData argument = new LuaLocalVariableData();

                argument.ReadFrom(process, functionData.localVariableDataAddress + (ulong)(i * LuaLocalVariableData.StructSize(process)));

                argumentList += (i == 0 ? "" : ", ") + argument.name;
            }

            return $"[{source}:{breakpointAdditionalData.instructionLine}]({argumentList})"; ;
        }

        DkmCompilerId IDkmSymbolCompilerIdQuery.GetCompilerId(DkmInstructionSymbol instruction, DkmInspectionSession inspectionSession)
        {
            return new DkmCompilerId(Guids.luaCompilerGuid, Guids.luaLanguageGuid);
        }

        DkmResolvedDocument[] IDkmSymbolDocumentCollectionQuery.FindDocuments(DkmModule module, DkmSourceFileId sourceFileId)
        {
            DkmCustomModuleInstance moduleInstance = module.GetModuleInstances().OfType<DkmCustomModuleInstance>().FirstOrDefault(el => el.Module.CompilerId.VendorId == Guids.luaCompilerGuid);

            if (moduleInstance == null)
                return module.FindDocuments(sourceFileId);

            var process = moduleInstance.Process;
            var processData = process.GetDataItem<LuaLocalProcessData>();

            foreach (var source in processData.symbolStore.knownSources)
            {
                if (source.Value.resolvedFileName == null)
                    source.Value.resolvedFileName = TryFindSourcePath(process.Path, processData, source.Key);

                var fileName = source.Value.resolvedFileName;

                if (sourceFileId.DocumentName == fileName)
                {
                    var dataItem = new LuaResolvedDocumentItem
                    {
                        source = source.Value
                    };

                    return new DkmResolvedDocument[1] { DkmResolvedDocument.Create(module, sourceFileId.DocumentName, null, DkmDocumentMatchStrength.FullPath, DkmResolvedDocumentWarning.None, false, dataItem) };
                }
            }

            // TODO: can we find a mapping from source line to loaded Lua scripts?
            return module.FindDocuments(sourceFileId);
        }

        bool FindFunctionInstructionForLine(DkmProcess process, LuaFunctionData function, int startLine, int endLine, out LuaFunctionData targetFunction, out int targetInstructionPointer, out int targetLine)
        {
            function.ReadLocalFunctions(process);
            function.ReadLineInfo(process);

            foreach (var localFunction in function.localFunctions)
            {
                if (FindFunctionInstructionForLine(process, localFunction, startLine, endLine, out targetFunction, out targetInstructionPointer, out targetLine))
                    return true;
            }

            // Check only first line in range
            int line = startLine;

            for (int instruction = 0; instruction < function.lineInfo.Length; instruction++)
            {
                if (function.lineInfo[instruction] == line)
                {
                    targetFunction = function;
                    targetInstructionPointer = instruction;
                    targetLine = line;
                    return true;
                }
            }

            targetFunction = null;
            targetInstructionPointer = 0;
            targetLine = 0;
            return false;
        }

        DkmInstructionSymbol[] IDkmSymbolDocumentSpanQuery.FindSymbols(DkmResolvedDocument resolvedDocument, DkmTextSpan textSpan, string text, out DkmSourcePosition[] symbolLocation)
        {
            var documentData = DebugHelpers.GetOrCreateDataItem<LuaResolvedDocumentItem>(resolvedDocument);

            if (documentData == null)
                return resolvedDocument.FindSymbols(textSpan, text, out symbolLocation);

            DkmCustomModuleInstance moduleInstance = resolvedDocument.Module.GetModuleInstances().OfType<DkmCustomModuleInstance>().FirstOrDefault(el => el.Module.CompilerId.VendorId == Guids.luaCompilerGuid);

            if (moduleInstance == null)
                return resolvedDocument.FindSymbols(textSpan, text, out symbolLocation);

            var process = moduleInstance.Process;

            foreach (var el in documentData.source.knownFunctions)
            {
                if (FindFunctionInstructionForLine(process, el.Value, textSpan.StartLine, textSpan.EndLine, out LuaFunctionData luaFunctionData, out int instructionPointer, out int line))
                {
                    var sourceFileId = DkmSourceFileId.Create(resolvedDocument.DocumentName, null, null, null);

                    var resultSpan = new DkmTextSpan(line, line, 0, 0);

                    symbolLocation = new DkmSourcePosition[1] { DkmSourcePosition.Create(sourceFileId, resultSpan) };

                    LuaAddressEntityData entityData = new LuaAddressEntityData
                    {
                        functionAddress = luaFunctionData.originalAddress,

                        instructionPointer = instructionPointer,

                        source = documentData.source.sourceFileName
                    };

                    LuaBreakpointAdditionalData additionalData = new LuaBreakpointAdditionalData
                    {
                        instructionLine = line
                    };

                    var entityDataBytes = entityData.Encode();
                    var additionalDataBytes = additionalData.Encode();

                    return new DkmInstructionSymbol[1] { DkmCustomInstructionSymbol.Create(resolvedDocument.Module, Guids.luaRuntimeGuid, entityDataBytes, (ulong)instructionPointer, additionalDataBytes) };
                }
            }

            return resolvedDocument.FindSymbols(textSpan, text, out symbolLocation);
        }

        Guid? CreateHelperFunctionBreakpoint(DkmNativeModuleInstance nativeModuleInstance, string functionName)
        {
            var functionAddress = DebugHelpers.TryGetFunctionAddressAtDebugStart(nativeModuleInstance, functionName);

            if (functionAddress != null)
            {
                Debug.WriteLine($"LuaDkmDebugger: Creating breakpoint in '{functionName}'");

                var nativeAddress = nativeModuleInstance.Process.CreateNativeInstructionAddress(functionAddress.Value);

                var breakpoint = DkmRuntimeInstructionBreakpoint.Create(Guids.luaSupportBreakpointGuid, null, nativeAddress, false, null);

                breakpoint.Enable();

                return breakpoint.UniqueId;
            }

            return null;
        }

        ulong FindFunctionAddress(DkmNativeModuleInstance nativeModuleInstance, string functionName)
        {
            var address = nativeModuleInstance.FindExportName(functionName, IgnoreDataExports: true);

            if (address != null)
            {
                Debug.WriteLine($"LuaDkmDebugger: Found helper library '{functionName}' function at 0x{address.CPUInstructionPart.InstructionPointer:x}");

                return address.CPUInstructionPart.InstructionPointer;
            }

            Debug.WriteLine($"LuaDkmDebugger: Failed to find helper library '{functionName}' function");

            return 0;
        }

        ulong FindVariableAddress(DkmNativeModuleInstance nativeModuleInstance, string variableName)
        {
            var address = nativeModuleInstance.FindExportName(variableName, IgnoreDataExports: false);

            if (address != null)
            {
                Debug.WriteLine($"LuaDkmDebugger: Found helper library '{variableName}' variable at 0x{address.CPUInstructionPart.InstructionPointer:x}");

                return address.CPUInstructionPart.InstructionPointer;
            }

            Debug.WriteLine($"LuaDkmDebugger: Failed to find helper library '{variableName}' variable");

            return 0;
        }

        void IDkmModuleInstanceLoadNotification.OnModuleInstanceLoad(DkmModuleInstance moduleInstance, DkmWorkList workList, DkmEventDescriptorS eventDescriptor)
        {
            var nativeModuleInstance = moduleInstance as DkmNativeModuleInstance;

            if (nativeModuleInstance != null)
            {
                var process = moduleInstance.Process;

                var processData = DebugHelpers.GetOrCreateDataItem<LuaLocalProcessData>(process);

                if (nativeModuleInstance.FullName != null && nativeModuleInstance.FullName.EndsWith(".exe"))
                {
                    // Check if Lua library is loaded
                    if (DebugHelpers.TryGetFunctionAddress(nativeModuleInstance, "lua_newstate").GetValueOrDefault(0) != 0)
                    {
                        Debug.WriteLine("LuaDkmDebugger: Found Lua library");

                        processData.moduleWithLoadedLua = nativeModuleInstance;
                    }

                    processData.executionStartAddress = DebugHelpers.TryGetFunctionAddressAtDebugStart(processData.moduleWithLoadedLua, "luaV_execute").GetValueOrDefault(0);
                    processData.executionEndAddress = DebugHelpers.TryGetFunctionAddressAtDebugEnd(processData.moduleWithLoadedLua, "luaV_execute").GetValueOrDefault(0);
                }

                if (nativeModuleInstance.FullName != null && nativeModuleInstance.FullName.EndsWith("kernel32.dll"))
                {
                    Debug.WriteLine("LuaDkmDebugger: Found kernel32 library");

                    processData.loadLibraryAddress = DebugHelpers.FindFunctionAddress(process.GetNativeRuntimeInstance(), "LoadLibraryA");
                }

                if (nativeModuleInstance.FullName != null && nativeModuleInstance.FullName.EndsWith("LuaDebugHelper_x86.dll"))
                {
                    Debug.WriteLine("LuaDkmDebugger: Found Lua debugger helper library");

                    var variableAddress = nativeModuleInstance.FindExportName("luaHelperIsInitialized", IgnoreDataExports: false);

                    if (variableAddress != null)
                    {
                        processData.helperHookFunctionAddress = FindFunctionAddress(nativeModuleInstance, "LuaHelperHook");

                        processData.helperBreakLineAddress = FindVariableAddress(nativeModuleInstance, "luaHelperBreakLine");
                        processData.helperStepOverAddress = FindVariableAddress(nativeModuleInstance, "luaHelperStepOver");
                        processData.helperStepIntoAddress = FindVariableAddress(nativeModuleInstance, "luaHelperStepInto");
                        processData.helperStepOutAddress = FindVariableAddress(nativeModuleInstance, "luaHelperStepOut");
                        processData.helperSkipDepthAddress = FindVariableAddress(nativeModuleInstance, "luaHelperSkipDepth");

                        processData.breakpointLuaHelperBreakpointHit = CreateHelperFunctionBreakpoint(nativeModuleInstance, "OnLuaHelperBreakpointHit").GetValueOrDefault(Guid.Empty);
                        processData.breakpointLuaHelperStepComplete = CreateHelperFunctionBreakpoint(nativeModuleInstance, "OnLuaHelperStepComplete").GetValueOrDefault(Guid.Empty);
                        processData.breakpointLuaHelperStepInto = CreateHelperFunctionBreakpoint(nativeModuleInstance, "OnLuaHelperStepInto").GetValueOrDefault(Guid.Empty);
                        processData.breakpointLuaHelperStepOut = CreateHelperFunctionBreakpoint(nativeModuleInstance, "OnLuaHelperStepOut").GetValueOrDefault(Guid.Empty);

                        // TODO: check all data

                        processData.helperStartAddress = nativeModuleInstance.BaseAddress;
                        processData.helperEndAddress = processData.helperStartAddress + nativeModuleInstance.Size;

                        // Tell remote component about helper library locations
                        var data = new HelperLocationsMessage
                        {
                            helperBreakLineAddress = processData.helperBreakLineAddress,
                            helperStepOverAddress = processData.helperStepOverAddress,
                            helperStepIntoAddress = processData.helperStepIntoAddress,
                            helperStepOutAddress = processData.helperStepOutAddress,
                            helperSkipDepthAddress = processData.helperSkipDepthAddress,

                            breakpointLuaHelperBreakpointHit = processData.breakpointLuaHelperBreakpointHit,
                            breakpointLuaHelperStepComplete = processData.breakpointLuaHelperStepComplete,
                            breakpointLuaHelperStepInto = processData.breakpointLuaHelperStepInto,
                            breakpointLuaHelperStepOut = processData.breakpointLuaHelperStepOut,

                            helperStartAddress = processData.helperStartAddress,
                            helperEndAddress = processData.helperEndAddress,

                            executionStartAddress = processData.executionStartAddress,
                            executionEndAddress = processData.executionEndAddress,
                        };

                        var message = DkmCustomMessage.Create(process.Connection, process, MessageToRemote.guid, MessageToRemote.luaHelperDataLocations, data, null);

                        message.SendLower();

                        // Handle Lua helper initialization sequence
                        var initialized = DebugHelpers.ReadIntVariable(process, variableAddress.CPUInstructionPart.InstructionPointer);

                        if (initialized.HasValue)
                        {
                            Debug.WriteLine($"LuaDkmDebugger: Found helper library init flag at 0x{variableAddress.CPUInstructionPart.InstructionPointer:x}");

                            if (initialized.Value == 0)
                            {
                                Debug.WriteLine("LuaDkmDebugger: Helper hasn't been initialized");

                                var breakpointId = CreateHelperFunctionBreakpoint(nativeModuleInstance, "OnLuaHelperInitialized");

                                if (breakpointId.HasValue)
                                {
                                    Debug.WriteLine("LuaDkmDebugger: Waiting for helper library initialization");

                                    processData.breakpointLuaHelperInitialized = breakpointId.Value;

                                    processData.helperInitializationWaitActive = true;
                                }
                            }
                            else if (initialized.Value == 1)
                            {
                                Debug.WriteLine("LuaDkmDebugger: Helper has been initialized");

                                processData.helperInitialized = true;
                            }
                        }
                    }

                    if (processData.helperInitializationWaitUsed && !processData.helperInitializationWaitActive)
                    {
                        Debug.WriteLine("LuaDkmDebugger: Lua thread is already suspended but the Helper initialization wait wasn't activated");

                        if (processData.helperInitializionSuspensionThread != null)
                        {
                            Debug.WriteLine("LuaDkmDebugger: Resuming Lua thread");

                            processData.helperInitializionSuspensionThread.Resume(true);

                            processData.helperInitializionSuspensionThread = null;
                        }
                    }
                }

                if (processData.moduleWithLoadedLua != null && processData.loadLibraryAddress != 0)
                {
                    // Check if already injected
                    if (processData.helperInjectRequested)
                        return;

                    processData.helperInjectRequested = true;

                    // TODO: helper function

                    // Track Lua state initialization (breakpoint at the end of the function)
                    {
                        var address = DebugHelpers.TryGetFunctionAddressAtDebugStart(processData.moduleWithLoadedLua, "lua_newstate");

                        if (address != null)
                        {
                            Debug.WriteLine("LuaDkmDebugger: Hooking Lua 'initialization mark' function");

                            var nativeAddress = process.CreateNativeInstructionAddress(address.Value);

                            var breakpoint = DkmRuntimeInstructionBreakpoint.Create(Guids.luaSupportBreakpointGuid, null, nativeAddress, false, null);

                            breakpoint.Enable();

                            processData.breakpointLuaInitialization = breakpoint.UniqueId;
                        }
                    }

                    // Track Lua state creation (breakpoint at the end of the function)
                    {
                        var address = DebugHelpers.TryGetFunctionAddressAtDebugEnd(processData.moduleWithLoadedLua, "lua_newstate");

                        if (address != null)
                        {
                            Debug.WriteLine("LuaDkmDebugger: Hooking Lua thread creation function");

                            var nativeAddress = process.CreateNativeInstructionAddress(address.Value);

                            var breakpoint = DkmRuntimeInstructionBreakpoint.Create(Guids.luaSupportBreakpointGuid, null, nativeAddress, false, null);

                            breakpoint.Enable();

                            processData.breakpointLuaThreadCreate = breakpoint.UniqueId;
                        }
                    }

                    // Track Lua state destruction (breakpoint at the start of the function)
                    {
                        var address = DebugHelpers.TryGetFunctionAddressAtDebugStart(processData.moduleWithLoadedLua, "lua_close");

                        if (address != null)
                        {
                            Debug.WriteLine("LuaDkmDebugger: Hooking Lua thread destruction function");

                            var nativeAddress = process.CreateNativeInstructionAddress(address.Value);

                            var breakpoint = DkmRuntimeInstructionBreakpoint.Create(Guids.luaSupportBreakpointGuid, null, nativeAddress, false, null);

                            breakpoint.Enable();

                            processData.breakpointLuaThreadDestroy = breakpoint.UniqueId;
                        }
                    }

                    string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                    string dllPathName = Path.Combine(assemblyFolder, "LuaDebugHelper_x86.dll");

                    if (!File.Exists(dllPathName))
                    {
                        Debug.WriteLine("LuaDkmDebugger: Helper dll hasn't been found");
                        return;
                    }

                    var dllNameAddress = process.AllocateVirtualMemory(0ul, 4096, 0x3000, 0x04);

                    byte[] bytes = Encoding.ASCII.GetBytes(dllPathName);

                    process.WriteMemory(dllNameAddress, bytes);
                    process.WriteMemory(dllNameAddress + (ulong)bytes.Length, new byte[1] { 0 });

                    var processHandle = Kernel32.OpenProcess(0x001F0FFF, false, process.LivePart.Id);

                    if (processHandle == IntPtr.Zero)
                    {
                        Debug.WriteLine("LuaDkmDebugger: Failed to open target process");
                        return;
                    }

                    var threadHandle = Kernel32.CreateRemoteThread(processHandle, IntPtr.Zero, UIntPtr.Zero, (IntPtr)processData.loadLibraryAddress, (IntPtr)dllNameAddress, 0, IntPtr.Zero);

                    if (threadHandle == IntPtr.Zero)
                    {
                        Debug.WriteLine("LuaDkmDebugger: Failed to start thread");
                        return;
                    }

                    processData.helperInjected = true;

                    Debug.WriteLine("LuaDkmDebugger: Helper library has been injected");
                }
            }
        }

        DkmCustomMessage IDkmCustomMessageCallbackReceiver.SendHigher(DkmCustomMessage customMessage)
        {
            var process = customMessage.Process;

            var processData = DebugHelpers.GetOrCreateDataItem<LuaLocalProcessData>(process);

            if (customMessage.MessageCode == MessageToLocal.luaSupportBreakpointHit)
            {
                var data = customMessage.Parameter1 as SupportBreakpointHitMessage;

                Debug.Assert(data != null);

                var thread = process.GetThreads().FirstOrDefault(el => el.UniqueId == data.threadId);

                if (data.breakpointId == processData.breakpointLuaInitialization)
                {
                    Debug.WriteLine("LuaDkmDebugger: Detected Lua initialization");

                    if (processData.helperInjected && !processData.helperInitialized && !processData.helperInitializationWaitUsed)
                    {
                        Debug.WriteLine("LuaDkmDebugger: Helper was injected but hasn't been initialized, suspening thread");

                        Debug.Assert(thread != null);

                        thread.Suspend(true);

                        processData.helperInitializionSuspensionThread = thread;
                        processData.helperInitializationWaitUsed = true;
                    }
                }
                else if (data.breakpointId == processData.breakpointLuaThreadCreate)
                {
                    Debug.WriteLine("LuaDkmDebugger: Detected Lua thread start");

                    const int CV_ALLREG_VFRAME = 0x00007536;
                    var vFrameRegister = DkmUnwoundRegister.Create(CV_ALLREG_VFRAME, new ReadOnlyCollection<byte>(BitConverter.GetBytes(data.vframe)));
                    var registers = thread.GetCurrentRegisters(new[] { vFrameRegister });
                    var instructionAddress = process.CreateNativeInstructionAddress(registers.GetInstructionPointer());
                    var frame = DkmStackWalkFrame.Create(thread, instructionAddress, data.frameBase, 0, DkmStackWalkFrameFlags.None, null, registers, null);

                    var inspectionSession = DkmInspectionSession.Create(process, null);

                    Debug.WriteLine($"LuaDkmDebugger: Evaluating Lua data at {DateTime.Now.Ticks / 10000.0}");

                    ulong? stateAddress = TryEvaluateAddressExpression($"L", inspectionSession, thread, frame, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);
                    long? version = TryEvaluateNumberExpression($"(int)*L->l_G->version", inspectionSession, thread, frame, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);
                    ulong? hookFunctionAddress = TryEvaluateAddressExpression($"&L->hook", inspectionSession, thread, frame, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);
                    ulong? hookBaseCountAddress = TryEvaluateAddressExpression($"&L->basehookcount", inspectionSession, thread, frame, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);
                    ulong? hookCountAddress = TryEvaluateAddressExpression($"&L->hookcount", inspectionSession, thread, frame, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);
                    ulong? hookMaskAddress = TryEvaluateAddressExpression($"&L->hookmask", inspectionSession, thread, frame, DkmEvaluationFlags.TreatAsExpression | DkmEvaluationFlags.NoSideEffects);

                    Debug.WriteLine($"LuaDkmDebugger: Completed evaluation at {DateTime.Now.Ticks / 10000.0}");

                    if (stateAddress.HasValue)
                    {
                        Debug.WriteLine($"LuaDkmDebugger: new Lua state 0x{stateAddress:x} version {version.GetValueOrDefault(501)}");

                        if (version.GetValueOrDefault(501) == 503)
                        {
                            // TODO: check evaluations
                            DebugHelpers.TryWriteVariable(process, hookFunctionAddress.Value, processData.helperHookFunctionAddress);
                            DebugHelpers.TryWriteVariable(process, hookBaseCountAddress.Value, 0);
                            DebugHelpers.TryWriteVariable(process, hookCountAddress.Value, 0);
                            DebugHelpers.TryWriteVariable(process, hookMaskAddress.Value, 7); // LUA_HOOKLINE | LUA_HOOKCALL | LUA_HOOKRET
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"LuaDkmDebugger: Failed to evaluate Luas state location");
                    }
                }
                else if (data.breakpointId == processData.breakpointLuaThreadDestroy)
                {
                    Debug.WriteLine("LuaDkmDebugger: Detected Lua thread destruction");
                }
                else if (data.breakpointId == processData.breakpointLuaHelperInitialized)
                {
                    Debug.WriteLine("LuaDkmDebugger: Detected Helper initialization");

                    processData.helperInitializationWaitActive = false;
                    processData.helperInitialized = true;

                    if (processData.helperInitializionSuspensionThread != null)
                    {
                        Debug.WriteLine("LuaDkmDebugger: Resuming Lua thread");

                        processData.helperInitializionSuspensionThread.Resume(true);

                        processData.helperInitializionSuspensionThread = null;
                    }
                }
            }

            return null;
        }
    }
}
