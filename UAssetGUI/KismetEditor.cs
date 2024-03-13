using Newtonsoft.Json.Linq;
using NodeEditor;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace UAssetGUI
{

    public class KismetEditor : Panel
    {
        public NodesControl NodeEditor;

        public KismetEditor()
        {
            NodeEditor = new NodesControl()
            {
                Visible = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Location = new System.Drawing.Point(0, 0),
                Name = "nodesControl",
                TabIndex = 0,
                Context = null,
            };

            Controls.Add(NodeEditor);
        }

        public class Value{}
        static Parameter PinExecute = new Parameter { Name = "execute", Direction = Direction.In, ParameterType = typeof(ExecutionPath) };
        static Parameter PinThen = new Parameter { Name = "then", Direction = Direction.Out, ParameterType = typeof(ExecutionPath) };
        static Parameter PinInValue = new Parameter { Name = "in", Direction = Direction.In, ParameterType = typeof(Value) };
        static Parameter PinOutValue = new Parameter { Name = "out", Direction = Direction.Out, ParameterType = typeof(Value) };

        internal struct JumpConnection
        {
            internal NodeVisual OutputNode;
            internal string OutputPin;
            internal uint InputIndex;
        }

        public void SetBytecode(UAsset asset, FunctionExport fn)
        {
            var bytecode = fn.ScriptBytecode;

            NodeEditor.Clear();

            var offsets = GetOffsets(bytecode).ToDictionary(l => l.Item1, l => l.Item2);
            var nodeMap = new Dictionary<KismetExpression, NodeVisual>();


            var jumpConnections = new List<JumpConnection>();

            NodeVisual BuildFunctionNode(FunctionExport fn, uint jump = 0)
            {
                var type = new CustomNodeType
                {
                    Name = fn.ObjectName.ToString(),
                    Parameters = new List<Parameter>{},
                };

                var node = new NodeVisual()
                {
                    Type = type,
                    Callable = false,
                    ExecInit = false,
                    Name = fn.ObjectName.ToString(),
                    NodeColor = System.Drawing.Color.Salmon,
                };

                type.Parameters.Add(PinThen);
                jumpConnections.Add(new JumpConnection { OutputNode = node, OutputPin = "then", InputIndex = jump });

                NodeEditor.AddNode(node, false);
                return node;
            }

            NodeVisual BuildExecNode(uint index, KismetExpression ex)
            {
                NodeVisual node;
                if (nodeMap.TryGetValue(ex, out node))
                    return node;

                var name = ex.GetType().Name;

                var type = new CustomNodeType
                {
                    Name = ex.GetType().Name,
                    Parameters = new List<Parameter>{},
                };

                node = new NodeVisual()
                {
                    Type = type,
                    Callable = false,
                    ExecInit = false,
                    Name = $"{index}: {name}",
                };

                void exec()
                {
                    type.Parameters.Add(PinExecute);
                }
                void jump(string name, uint to)
                {
                    type.Parameters.Add(new Parameter { Name = name, Direction = Direction.Out, ParameterType = typeof(ExecutionPath) });
                    jumpConnections.Add(new JumpConnection { OutputNode = node, OutputPin = name, InputIndex = to });
                }
                void then(string name = "then")
                {
                    jump(name, index + GetSize(ex));
                }
                void input(string name, KismetExpression ex)
                {
                    type.Parameters.Add(new Parameter { Name = name, Direction = Direction.In, ParameterType = typeof(Value) });
                    var variable = BuildExpressionNode(ex);
                    NodeEditor.graph.Connections.Add(new NodeConnection { OutputNode = variable, OutputSocketName = "out", InputNode = node, InputSocketName = name });
                }

                switch (ex)
                {
                    case EX_EndOfScript:
                        node.Name = $"{index}: End of Script";
                        break;
                    case EX_Return:
                        node.Name = $"{index}: Return";
                        exec();
                        break;
                    case EX_ComputedJump e:
                        exec();
                        input("offset", e.CodeOffsetExpression);
                        node.Name = $"{index}: Computed Jump";
                        break;
                    case EX_Jump e:
                        exec();
                        jump("then", e.CodeOffset);
                        node.Name = $"{index}: Jump";
                        break;
                    case EX_JumpIfNot e:
                        exec();
                        then("true");
                        jump("false", e.CodeOffset);
                        input("condition", e.BooleanExpression);
                        node.Name = $"{index}: Branch";
                        break;
                    case EX_PushExecutionFlow e:
                        exec();
                        then("first");
                        jump("then", e.PushingAddress);
                        node.Name = $"{index}: Push Execution";
                        break;
                    case EX_PopExecutionFlow:
                        exec();
                        node.Name = $"{index}: Pop Execution";
                        break;
                    case EX_PopExecutionFlowIfNot e:
                        exec(); then();
                        input("condition", e.BooleanExpression);
                        node.Name = $"{index}: Pop Execution If False";
                        break;
                    case EX_LetObj e:
                        exec(); then();
                        input("variable", e.VariableExpression);
                        input("value", e.AssignmentExpression);
                        node.Name = $"{index}: Set Obj";
                        break;
                    case EX_Let e:
                        exec(); then();
                        input("variable", e.Variable);
                        input("value", e.Expression);
                        node.Name = $"{index}: Set Value";
                        break;
                    case EX_LetBool e:
                        exec(); then();
                        input("variable", e.VariableExpression);
                        input("value", e.AssignmentExpression);
                        node.Name = $"{index}: Set Bool";
                        break;
                    case EX_SetArray e:
                        {
                            int i = 0;
                            exec(); then();
                            input("variable", e.AssigningProperty);
                            foreach (var element in e.Elements)
                            {
                                input($"element {i}", element);
                                i++;
                            }
                            node.Name = $"{index}: Set Array";
                            break;
                        }
                    case EX_Context e:
                        exec(); then();
                        input("object", e.ObjectExpression);
                        input("member", e.ContextExpression);
                        node.Name = $"{index}: Context";
                        break;
                    case EX_CallMath e:
                        {
                            exec(); then();
                            int i = 1;
                            foreach (var param in e.Parameters)
                            {
                                input($"arg_{i}", param);
                                i++;
                            }
                            string functionName = UAssetAPI.Kismet.KismetSerializer.GetName(e.StackNode.Index);

                            if (functionName == "Delay"){
                                jump("delayed", ((EX_SkipOffsetConst)((EX_StructConst)e.Parameters[2]).Value[0]).Value);
                            }
                            node.Name = $"{index}: CallMath - " + UAssetAPI.Kismet.KismetSerializer.GetName(e.StackNode.Index);
                            break;
                        }
                    case EX_LocalFinalFunction e:
                        {
                            exec(); then();
                            int i = 1;
                            foreach (var param in e.Parameters)
                            {
                                input($"arg_{i}", param);
                                i++;
                            }
                            node.Name = $"{index}: LocalFinalFunction - " + UAssetAPI.Kismet.KismetSerializer.GetName(e.StackNode.Index);
                            break;
                        }
                    case EX_FinalFunction e:
                        {
                            exec(); then();
                            int i = 1;
                            foreach (var param in e.Parameters)
                            {
                                input($"arg_{i}", param);
                                i++;
                            }
                            node.Name = $"{index}: FinalFunction - " + UAssetAPI.Kismet.KismetSerializer.GetName(e.StackNode.Index);
                            break;
                        }
                    case EX_VirtualFunction e:
                        {
                            exec(); then();
                            int i = 1;
                            foreach (var param in e.Parameters)
                            {
                                input($"arg_{i}", param);
                                i++;
                            }
                            node.Name = $"{index}: VirtualFunction - " + e.VirtualFunctionName.ToString();
                            break;
                        }
                    case EX_LetValueOnPersistentFrame e:
                        {
                            exec(); then();
                            String fullName = UAssetAPI.Kismet.KismetSerializer.SerializePropertyPointer(e.DestinationProperty, new[] { "Property Name" })[0].Value.ToString();
                            node.Name = $"{index}: LetValueOnPersistentFrame - " + fullName.Substring(fullName.LastIndexOf('.') + 1);
                            input("value", e.AssignmentExpression);
                            break;
                        }
                    case EX_AddMulticastDelegate e:
                        {
                            exec(); then();
                            node.Name = $"{index}: Add MulticastDelegate";
                            input("MulticastDelegate", e.Delegate);
                            input("Delegate", e.DelegateToAdd);
                            break;
                        }
                    case EX_RemoveMulticastDelegate e:
                        {
                            exec(); then();
                            node.Name = $"{index}: Remove MulticastDelegate";
                            input("MulticastDelegate", e.Delegate);
                            input("Delegate", e.DelegateToAdd);
                            break;
                        }
                    case EX_ClearMulticastDelegate e:
                        {
                            exec(); then();
                            node.Name = $"{index}: Clear MulticastDelegate";
                            input("MulticastDelegate", e.DelegateToClear);
                            break;
                        }
                    case EX_BindDelegate e:
                        {
                            exec(); then();
                            node.Name = $"{index}: Bind Delegate: " + e.FunctionName.ToString();
                            input("Delegate", e.Delegate);
                            input("Object", e.ObjectTerm);
                            break;
                        }

                    default:
                        exec(); then();
                        node.NodeColor = System.Drawing.Color.Orange;
                        break;
                };

                nodeMap.Add(ex, node);
                NodeEditor.AddNode(node, false);
                return node;
            }

            NodeVisual BuildExpressionNode(KismetExpression ex)
            {
                var type = new CustomNodeType
                {
                    Name = ex.GetType().Name,
                    Parameters = new List<Parameter>{},
                };

                var node = new NodeVisual()
                {
                    Type = type,
                    Callable = false,
                    ExecInit = false,
                    Name = type.Name,
                };

                switch (ex)
                {
                    case EX_Self:
                        node.Name = "Self";
                        break;
                    case EX_LocalVariable e:
                        {
                            String fullName = UAssetAPI.Kismet.KismetSerializer.SerializePropertyPointer(e.Variable, new[] { "Variable Name" })[0].Value.ToString();
                            node.Name = "LocalVariable\n\n" + fullName.Substring(fullName.LastIndexOf('.') + 1);
                            break;
                        }
                    case EX_LocalOutVariable e:
                        {
                            String fullName = UAssetAPI.Kismet.KismetSerializer.SerializePropertyPointer(e.Variable, new[] { "Variable Name" })[0].Value.ToString();
                            node.Name = "LocalOut\n\n" + fullName.Substring(fullName.LastIndexOf('.') + 1);
                            break;
                        }
                    case EX_InstanceVariable e:
                        {
                            String fullName = UAssetAPI.Kismet.KismetSerializer.SerializePropertyPointer(e.Variable, new[] { "Variable Name" })[0].Value.ToString();
                            node.Name = "InstanceVariable\n\n" + fullName.Substring(fullName.LastIndexOf('.') + 1);
                            break;
                        }
                    //case EX_ComputedJump:
                    //case EX_NoObject:
                    //case EX_IntOne:
                    //case EX_IntZero:
                    case EX_SwitchValue:
                        node.Name = "Switch";
                        break;
                    case EX_IntConst e:
                        node.Name = "Int\n\n" + e.Value.ToString();
                        break;
                    case EX_NameConst e:
                        node.Name = "Name\n\n" + e.Value.ToString();
                        break;
                    case EX_True:
                        node.Name = "Bool\n\nTrue";
                        break;
                    case EX_False:
                        node.Name = "Bool\n\nFalse";
                        break;
                    case EX_ByteConst e:
                        node.Name = "Byte\n\n" + e.Value.ToString();
                        break;
                    case EX_SkipOffsetConst e:
                        node.Name = "Skip Offset\n\n" + e.Value.ToString();
                        break;
                    case EX_NoObject:
                        node.Name = "Null Reference";
                        break;
                    //case EX_Nothing:
                    case EX_ObjectConst e:
                        node.Name = "Object\n\n" + UAssetAPI.Kismet.KismetSerializer.GetFullName(e.Value.Index);
                        break;
                    case EX_FloatConst e:
                        node.Name = "Float\n\n" + e.Value.ToString();
                        break;
                    case EX_StringConst e:
                        node.Name = "String\n\n" + e.Value;
                        break;
                    case EX_UnicodeStringConst e:
                        node.Name = "UniString\n\n" + e.Value;
                        break;
                    case EX_UInt64Const e:
                        node.Name = "UInt64\n\n" + e.Value;
                        break;
                    case EX_Int64Const e:
                        node.Name = "Int64\n\n" + e.Value;
                        break;
                    case EX_CallMath e:
                        node.Name = "CallMath: " + UAssetAPI.Kismet.KismetSerializer.GetName(e.StackNode.Index);
                        break;
                    case EX_LocalFinalFunction e:
                        node.Name = "LocFinalFunc: " + UAssetAPI.Kismet.KismetSerializer.GetName(e.StackNode.Index);
                        break;
                    case EX_FinalFunction e:
                        node.Name = "FinalFunc: " + UAssetAPI.Kismet.KismetSerializer.GetName(e.StackNode.Index);
                        break;
                    case EX_VirtualFunction e:
                        node.Name = "VirtualFunc: " + e.VirtualFunctionName.ToString();
                        break;
                    case EX_Context e:
                        node.Name = "Context";
                        break;
                    case EX_StructConst e:
                        node.Name = "Struct";
                        break;
                    case EX_StructMemberContext e:
                        {
                            String fullName = UAssetAPI.Kismet.KismetSerializer.SerializePropertyPointer(e.StructMemberExpression, new[] { "Property Name" })[0].Value.ToString();
                            node.Name = "Struct Member: " + fullName.Substring(fullName.LastIndexOf('.') + 1);
                            break;
                        }
                    case EX_PrimitiveCast e:
                        {
                            switch (e.ConversionType)
                            {
                                case ECastToken.InterfaceToBool:
                                    node.Name = "Primitive Cast: Interface To Bool";
                                    break;
                                case ECastToken.ObjectToBool:
                                    node.Name = "Primitive Cast: Object To Bool";
                                    break;
                                case ECastToken.ObjectToInterface:
                                    node.Name = "Primitive Cast: Object To Interface";
                                    break;
                                default:
                                    node.Name = "EX_PrimitiveCast: Unknown";
                                    break;
                            }
                            break;
                        }
                    case EX_DynamicCast e:
                        node.Name = "Dynamic Cast to " + UAssetAPI.Kismet.KismetSerializer.GetName(e.ClassPtr.Index);
                        break;
                    //case EX_InterfaceContext e:
                    default:
                        //node.Name = "Default:" + node.Name;
                        break;
                }


                type.Parameters.Add(PinOutValue);

                void exp(string name, KismetExpression ex)
                {
                    var variable = BuildExpressionNode(ex);
                    type.Parameters.Add(new Parameter { Name = name, Direction = Direction.In, ParameterType = typeof(Value) });
                    NodeEditor.graph.Connections.Add(new NodeConnection { OutputNode = variable, OutputSocketName = "out", InputNode = node, InputSocketName = name });
                }

                switch (ex)
                {
                    case EX_Self:
                    case EX_LocalVariable:
                    case EX_LocalOutVariable:
                    case EX_InstanceVariable:
                    case EX_ComputedJump:
                    case EX_NoObject:
                    case EX_IntOne:
                    case EX_IntZero:
                    case EX_IntConst:
                    case EX_True:
                    case EX_False:
                    case EX_ByteConst:
                    case EX_Nothing:
                    case EX_ObjectConst:
                    case EX_FloatConst:
                    case EX_StringConst:
                    case EX_UnicodeStringConst:
                    case EX_UInt64Const:
                    case EX_Int64Const:
                        //type.CustomEditor = typeof(TextBox);
                        break;
                    case EX_CallMath e:
                        {
                            int i = 1;
                            foreach (var param in e.Parameters)
                            {
                                exp($"arg_{i}", param);
                                i++;
                            }
                            break;
                        }
                    case EX_LocalFinalFunction e:
                        {
                            int i = 1;
                            foreach (var param in e.Parameters)
                            {
                                exp($"arg_{i}", param);
                                i++;
                            }
                            break;
                        }
                    case EX_FinalFunction e:
                        {
                            int i = 1;
                            foreach (var param in e.Parameters)
                            {
                                exp($"arg_{i}", param);
                                i++;
                            }
                            break;
                        }
                    case EX_VirtualFunction e:
                        {
                            int i = 1;
                            foreach (var param in e.Parameters)
                            {
                                exp($"arg_{i}", param);
                                i++;
                            }
                            break;
                        }
                    case EX_Context e:
                        {
                            exp("object", e.ObjectExpression);
                            exp("member", e.ContextExpression);
                            break;
                        }
                    case EX_InterfaceContext e:
                        {
                            exp("interface", e.InterfaceValue);
                            break;
                        }
                    case EX_SwitchValue e:
                        {
                            int i = 1;
                            foreach (var Case in e.Cases)
                            {
                                exp($"case_{i}_value", Case.CaseIndexValueTerm);
                                exp($"case_{i}_result", Case.CaseTerm);
                                i++;
                            }
                            exp("index", e.IndexTerm);
                        }
                        break;
                    case EX_StructConst e:
                        {
                            int index = 1;
                            foreach (var property in e.Value)
                            {
                                exp($"property {index}", property);
                                index++;
                            }
                            break;
                        }
                    case EX_PrimitiveCast e:
                        {
                            exp("expression", e.Target);
                            break;
                        }
                    case EX_DynamicCast e:
                        {
                            exp("expression", e.TargetExpression);
                            break;
                        }
                    case EX_StructMemberContext e:
                        exp("struct", e.StructExpression);
                        break;
                    default:
                        Console.WriteLine($"unimplemented {ex}");
                        break;
                }

                if (node.Name.Contains("EX_"))
                {
                    node.NodeColor = System.Drawing.Color.Orange;
                }

                nodeMap.Add(ex, node);
                NodeEditor.AddNode(node, false);
                return node;
            }

            BuildFunctionNode(fn);

            if (fn.ObjectName.ToString().StartsWith("ExecuteUbergraph"))
            {
                foreach (var export in asset.Exports)
                {
                    if (export is FunctionExport f)
                    {
                        foreach (var ex in f.ScriptBytecode)
                        {
                            if (ex is EX_FinalFunction ff
                                    && ff.StackNode.IsExport()
                                    && ff.StackNode.ToExport(asset) == fn
                                    && ff.Parameters.Length == 1
                                    && ff.Parameters[0] is EX_IntConst j)
                            {
                                BuildFunctionNode(f, (uint) j.Value);
                            }
                        }
                    }
                }
            }

            uint index = 0;
            foreach (var ex in bytecode)
            {
                var node = BuildExecNode(index, ex);
                index += GetSize(ex);
            }
            foreach (var jump in jumpConnections)
            {
                KismetExpression ex;
                if (!offsets.TryGetValue(jump.InputIndex, out ex))
                {
                    Console.WriteLine($"could not find expression at {jump.InputIndex}");
                    continue;
                }
                NodeVisual node;
                if (!nodeMap.TryGetValue(ex, out node))
                {
                    Console.WriteLine($"could not find node at {jump.InputIndex}");
                    continue;
                }

                var conn = new NodeConnection
                {
                    OutputNode = jump.OutputNode,
                    OutputSocketName = jump.OutputPin,
                    InputNode = node,
                    InputSocketName = "execute",
                };
                //Console.WriteLine($"{jump.OutputNode} {node}");
                //Console.WriteLine($"{conn.OutputSocket} {conn.InputSocket}");
                NodeEditor.graph.Connections.Add(conn);
            }

            LayoutNodes();

            NodeEditor.Refresh();
            NodeEditor.needRepaint = true;
        }

        public void LayoutNodes()
        {
            try
            {
                var info = new ProcessStartInfo("dot.exe");
                info.Arguments = "-Tplain -y";
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardInput = true;
                var p = Process.Start(info);

                var dot = p.StandardInput;

                var functionNodes = new List<int>();
                dot.WriteLine("strict digraph {");
                dot.WriteLine("rankdir=\"LR\"");
                var nodeDict = NodeEditor.graph.Nodes.Select((v, i) => (v, i)).ToDictionary(p => p.v, p => p.i);
                foreach (var entry in nodeDict)
                {
                    var inputs = String.Join(" | ", entry.Key.GetInputs().Select(p => $"<{p.Name}>{p.Name}"));
                    var outputs = String.Join(" | ", entry.Key.GetOutputs().Select(p => $"<{p.Name}>{p.Name}"));
                    dot.WriteLine($"{entry.Value} [shape=\"record\", width={entry.Key.GetNodeBounds().Width*4/NodeVisual.NodeWidth}, label=\"{{{{ {{{entry.Key.Name}}} | {{ {{ {inputs} }} | {{ {outputs} }} }} | footer }}}}\"]");
                    if (entry.Key.NodeColor == System.Drawing.Color.Salmon) // TODO possibly worst way to detect special nodes ever
                    {
                        functionNodes.Add(entry.Value);
                    }
                }
                foreach (var conn in NodeEditor.graph.Connections)
                {
                    var weight = conn.GetType() == typeof(ExecutionPath) ? 3 : 1; // can't tell if this is actually doing anything
                    dot.WriteLine($"{nodeDict[conn.OutputNode]}:{conn.OutputSocketName}:e -> {nodeDict[conn.InputNode]}:{conn.InputSocketName}:w [weight = {weight}]");
                }

                dot.WriteLine("{");
                dot.WriteLine("rank = \"source\";");
                foreach (var fn in functionNodes)
                {
                    dot.WriteLine(fn);
                }
                dot.WriteLine("}");

                dot.WriteLine("}");
                dot.Close();

                var scaleX = 50.0f;
                var scaleY = 60.0f;
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    var split = line.Split(' ');
                    switch (split[0])
                    {
                        case "node":
                            var node = NodeEditor.graph.Nodes[Int32.Parse(split[1])];
                            node.X = float.Parse(split[2], CultureInfo.InvariantCulture) * scaleX;
                            node.Y = float.Parse(split[3], CultureInfo.InvariantCulture) * scaleY;
                            node.LayoutEditor(NodeEditor.Zoom);
                            break;
                    }
                }

                p.WaitForExit();
            }
            catch (Win32Exception e)
            {
                Console.WriteLine("Failed to find and start 'dot' (Graphviz), nodes will not be laid out");
                Console.WriteLine(e);
            }
        }

        public static IEnumerable<(uint, KismetExpression)> GetOffsets(KismetExpression[] bytecode) {
            var offsets = new List<(uint, KismetExpression)>();
            uint offset = 0;
            foreach (var inst in bytecode) {
                offsets.Add((offset, inst));
                offset += GetSize(inst);
            }
            return offsets;
        }

        public static void Walk(KismetExpression ex, Action<KismetExpression> func) {
            uint offset = 0;
            Walk(ref offset, ex, (e, o) => func(e));
        }

        public static void Walk(ref uint offset, KismetExpression ex, Action<KismetExpression, uint> func) {
            func(ex, offset);
            offset++;
            switch (ex) {
                case EX_FieldPathConst e:
                    Walk(ref offset, e.Value, func);
                    break;
                case EX_SoftObjectConst e:
                    Walk(ref offset, e.Value, func);
                    break;
                case EX_AddMulticastDelegate e:
                    Walk(ref offset, e.Delegate, func);
                    Walk(ref offset, e.DelegateToAdd, func);
                    break;
                case EX_ArrayConst e:
                    offset += 8;
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    break;
                case EX_ArrayGetByRef e:
                    Walk(ref offset, e.ArrayVariable, func);
                    Walk(ref offset, e.ArrayIndex, func);
                    break;
                case EX_Assert e:
                    offset += 3;
                    Walk(ref offset, e.AssertExpression, func);
                    break;
                case EX_BindDelegate e:
                    offset += 12;
                    Walk(ref offset, e.Delegate, func);
                    Walk(ref offset, e.ObjectTerm, func);
                    break;
                case EX_CallMath e:
                    offset += 8;
                    foreach (var p in e.Parameters) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_CallMulticastDelegate e:
                    offset += 8;
                    Walk(ref offset, e.Delegate, func);
                    foreach (var p in e.Parameters) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_ClearMulticastDelegate e:
                    Walk(ref offset, e.DelegateToClear, func);
                    break;
                case EX_ComputedJump e:
                    Walk(ref offset, e.CodeOffsetExpression, func);
                    break;
                case EX_Context e: // +EX_Context_FailSilent +EX_ClassContext
                    Walk(ref offset, e.ObjectExpression, func);
                    offset += 12;
                    Walk(ref offset, e.ContextExpression, func);
                    break;
                case EX_CrossInterfaceCast e:
                    offset += 8;
                    Walk(ref offset, e.Target, func);
                    break;
                case EX_DynamicCast e:
                    offset += 8;
                    Walk(ref offset, e.TargetExpression, func);
                    break;
                case EX_FinalFunction e: // +EX_LocalFinalFunction
                    offset += 8;
                    foreach (var p in e.Parameters) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_InterfaceContext e:
                    Walk(ref offset, e.InterfaceValue, func);
                    break;
                case EX_InterfaceToObjCast e:
                    offset += 8;
                    Walk(ref offset, e.Target, func);
                    break;
                case EX_JumpIfNot e:
                    offset += 4;
                    Walk(ref offset, e.BooleanExpression, func);
                    break;
                case EX_Let e:
                    offset += 8;
                    Walk(ref offset, e.Variable, func);
                    Walk(ref offset, e.Expression, func);
                    break;
                case EX_LetBool e:
                    Walk(ref offset, e.VariableExpression, func);
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_LetDelegate e:
                    Walk(ref offset, e.VariableExpression, func);
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_LetMulticastDelegate e:
                    Walk(ref offset, e.VariableExpression, func);
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_LetObj e:
                    Walk(ref offset, e.VariableExpression, func);
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_LetValueOnPersistentFrame e:
                    offset += 8;
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_LetWeakObjPtr e:
                    Walk(ref offset, e.VariableExpression, func);
                    Walk(ref offset, e.AssignmentExpression, func);
                    break;
                case EX_VirtualFunction e: // +EX_LocalVirtualFunction
                    offset += 12;
                    foreach (var p in e.Parameters) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_MapConst e:
                    offset += 20;
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    break;
                case EX_MetaCast e:
                    offset += 8;
                    Walk(ref offset, e.TargetExpression, func);
                    break;
                case EX_ObjToInterfaceCast e:
                    offset += 8;
                    Walk(ref offset, e.Target, func);
                    break;
                case EX_PopExecutionFlowIfNot e:
                    Walk(ref offset, e.BooleanExpression, func);
                    break;
                case EX_PrimitiveCast e:
                    offset += 1;
                    Walk(ref offset, e.Target, func);
                    break;
                case EX_RemoveMulticastDelegate e:
                    Walk(ref offset, e.Delegate, func);
                    Walk(ref offset, e.DelegateToAdd, func);
                    break;
                case EX_Return e:
                    Walk(ref offset, e.ReturnExpression, func);
                    break;
                case EX_SetArray e:
                    Walk(ref offset, e.AssigningProperty, func);
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_SetConst e:
                    offset += 12;
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_SetMap e:
                    Walk(ref offset, e.MapProperty, func);
                    offset += 4;
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    break;
                case EX_SetSet e:
                    Walk(ref offset, e.SetProperty, func);
                    offset += 4;
                    foreach (var p in e.Elements) Walk(ref offset, p, func);
                    break;
                case EX_Skip e:
                    offset += 4;
                    Walk(ref offset, e.SkipExpression, func);
                    break;
                case EX_StructConst e:
                    offset += 12;
                    foreach (var p in e.Value) Walk(ref offset, p, func);
                    offset += 1;
                    break;
                case EX_StructMemberContext e:
                    offset += 8;
                    Walk(ref offset, e.StructExpression, func);
                    break;
                case EX_SwitchValue e:
                    offset += 6;
                    Walk(ref offset, e.IndexTerm, func);
                    foreach (var p in e.Cases) {
                        Walk(ref offset, p.CaseIndexValueTerm, func);
                        offset += 4;
                        Walk(ref offset, p.CaseTerm, func);
                    }
                    Walk(ref offset, e.DefaultTerm, func);
                    break;
                default:
                    offset += GetSize(ex) - 1;
                    break;
            }
        }
        public static uint GetSize(KismetExpression exp)
        {
            return 1 + exp switch
            {
                EX_PushExecutionFlow => 4,
                EX_ComputedJump e => GetSize(e.CodeOffsetExpression),
                EX_Jump e => 4,
                EX_JumpIfNot e => 4 + GetSize(e.BooleanExpression),
                EX_LocalVariable e => 8,
                EX_DefaultVariable e => 8,
                EX_ObjToInterfaceCast e => 8 + GetSize(e.Target),
                EX_Let e => 8 + GetSize(e.Variable) + GetSize(e.Expression),
                EX_LetObj e => GetSize(e.VariableExpression) + GetSize(e.AssignmentExpression),
                EX_LetBool e => GetSize(e.VariableExpression) + GetSize(e.AssignmentExpression),
                EX_LetWeakObjPtr e => GetSize(e.VariableExpression) + GetSize(e.AssignmentExpression),
                EX_LetValueOnPersistentFrame e => 8 + GetSize(e.AssignmentExpression),
                EX_StructMemberContext e => 8 + GetSize(e.StructExpression),
                EX_MetaCast e => 8 + GetSize(e.TargetExpression),
                EX_DynamicCast e => 8 + GetSize(e.TargetExpression),
                EX_PrimitiveCast e => 1 + e.ConversionType switch { ECastToken.ObjectToInterface => 8U, /* TODO InterfaceClass */ _ => 0U} + GetSize(e.Target),
                EX_PopExecutionFlow e => 0,
                EX_PopExecutionFlowIfNot e => GetSize(e.BooleanExpression),
                EX_CallMath e => 8 + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_SwitchValue e => 6 + GetSize(e.IndexTerm) + e.Cases.Select(c => GetSize(c.CaseIndexValueTerm) + 4 + GetSize(c.CaseTerm)).Aggregate(0U, (acc, x) => x + acc) + GetSize(e.DefaultTerm),
                EX_Self => 0,
                EX_TextConst e =>
                    1 + e.Value.TextLiteralType switch
                    {
                        EBlueprintTextLiteralType.Empty => 0,
                        EBlueprintTextLiteralType.LocalizedText => GetSize(e.Value.LocalizedSource) + GetSize(e.Value.LocalizedKey) + GetSize(e.Value.LocalizedNamespace),
                        EBlueprintTextLiteralType.InvariantText => GetSize(e.Value.InvariantLiteralString),
                        EBlueprintTextLiteralType.LiteralString => GetSize(e.Value.LiteralString),
                        EBlueprintTextLiteralType.StringTableEntry => 8 + GetSize(e.Value.StringTableId) + GetSize(e.Value.StringTableKey),
                        _ => throw new NotImplementedException(),
                    },
                EX_ObjectConst e => 8,
                EX_VectorConst e => 12,
                EX_RotationConst e => 12,
                EX_TransformConst e => 40,
                EX_Context e => + GetSize(e.ObjectExpression) + 4 + 8 + GetSize(e.ContextExpression),
                EX_CallMulticastDelegate e => 8 + GetSize(e.Delegate) + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_LocalFinalFunction e => 8 + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_FinalFunction e => 8 + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_LocalVirtualFunction e => 12 + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_VirtualFunction e => 12 + e.Parameters.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_InstanceVariable e => 8,
                EX_AddMulticastDelegate e => GetSize(e.Delegate) + GetSize(e.DelegateToAdd),
                EX_RemoveMulticastDelegate e => GetSize(e.Delegate) + GetSize(e.DelegateToAdd),
                EX_ClearMulticastDelegate e => GetSize(e.DelegateToClear),
                EX_BindDelegate e => 12 + GetSize(e.Delegate) + GetSize(e.ObjectTerm),
                EX_StructConst e => 8 + 4 + e.Value.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_SetArray e => GetSize(e.AssigningProperty) + e.Elements.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_SetMap e => GetSize(e.MapProperty) + 4 + e.Elements.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_SetSet e => GetSize(e.SetProperty) + 4 + e.Elements.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_SoftObjectConst e => GetSize(e.Value),
                EX_ByteConst e => 1,
                EX_IntConst e => 4,
                EX_FloatConst e => 4,
                EX_Int64Const e => 8,
                EX_UInt64Const e => 8,
                EX_NameConst e => 12,
                EX_StringConst e => (uint) e.Value.Length + 1,
                EX_UnicodeStringConst e => 2 * ((uint) e.Value.Length + 1),
                EX_SkipOffsetConst e => 4,
                EX_ArrayConst e => 12 + e.Elements.Select(p => GetSize(p)).Aggregate(0U, (acc, x) => x + acc) + 1,
                EX_Return e => GetSize(e.ReturnExpression),
                EX_LocalOutVariable e => 8,
                EX_InterfaceContext e => GetSize(e.InterfaceValue),
                EX_InterfaceToObjCast e => 8 + GetSize(e.Target),
                EX_ArrayGetByRef e => GetSize(e.ArrayVariable) + GetSize(e.ArrayIndex),
                EX_True e => 0,
                EX_False e => 0,
                EX_Nothing e => 0,
                EX_NoObject e => 0,
                EX_EndOfScript e => 0,
                EX_Tracepoint e => 0,
                EX_WireTracepoint e => 0,
                _ => throw new NotImplementedException(exp.ToString()),
            };
        }
    }
}
