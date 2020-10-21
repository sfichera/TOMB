﻿using Phantasma.Domain;
using Phantasma.Numerics;
using Phantasma.VM;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Phantasma.Tomb.Compiler
{
    public class Compiler
    {
        private List<LexerToken> tokens;
        private int tokenIndex = 0;

        private int currentLabel = 0;

        public int CurrentLine { get; private set; }
        public int CurrentColumn { get; private set; }

        private string[] lines;

        public static Compiler Instance { get; private set; }

        public Compiler()
        {
            Instance = this;
        }

        private void Rewind(int steps = 1)
        {
            tokenIndex -= steps;
            if (tokenIndex < 0)
            {
                throw new CompilerException("unexpected rewind");
            }
        }

        public int AllocateLabel()
        {
            currentLabel++;
            return currentLabel;
        }

        private bool HasTokens()
        {
            return tokenIndex < tokens.Count;
        }

        private LexerToken FetchToken()
        {
            if (tokenIndex >= tokens.Count)
            {
                throw new CompilerException("unexpected end of file");
            }

            var token = tokens[tokenIndex];
            tokenIndex++;

            this.CurrentLine = token.line;
            this.CurrentColumn = token.column;

            //Console.WriteLine(token);
            return token;
        }

        private void ExpectToken(string val)
        {
            var token = FetchToken();

            if (token.value != val)
            {
                throw new CompilerException("expected " + val);
            }
        }

        private string ExpectKind(TokenKind expectedKind)
        {
            var token = FetchToken();

            if (token.kind != expectedKind)
            {
                throw new CompilerException($"expected {expectedKind}, got {token.kind} instead");
            }

            return token.value;
        }

        private string ExpectIdentifier()
        {
            return ExpectKind(TokenKind.Identifier);
        }

        private string[] ExpectAsm()
        {
            return ExpectKind(TokenKind.Asm).Split('\n').Select(x => x.TrimStart()).ToArray();
        }

        private string ExpectString()
        {
            return ExpectKind(TokenKind.String);
        }

        private string ExpectNumber()
        {
            return ExpectKind(TokenKind.Number);
        }

        private string ExpectBool()
        {
            return ExpectKind(TokenKind.Bool);
        }

        private VarType ExpectType()
        {
            var token = FetchToken();

            if (token.kind != TokenKind.Type)
            {
                if (_structs.ContainsKey(token.value))
                {
                    return VarType.Find(VarKind.Struct, token.value);
                }

                throw new CompilerException("expected type, got " + token.kind);
            }

            var kind = (VarKind)Enum.Parse(typeof(VarKind), token.value, true);
            return VarType.Find(kind);
        }

        public string GetLine(int index)
        {
            if (index <= 0 || index > lines.Length)
            {
                return "";
            }

            return lines[index - 1];
        }

        private List<Module> _modules = new List<Module>();
        private Dictionary<string, StructDeclaration> _structs = new Dictionary<string, StructDeclaration>();

        private Module FindModule(string name)
        {
            foreach (var entry in _modules)
            {
                if (entry.Name == name)
                {
                    return entry;
                }
            }

            return null;
        }

        public Module[] Process(string sourceCode)
        {
            this.tokens = Lexer.Process(sourceCode);

            /*foreach (var token in tokens)
            {
                Console.WriteLine(token);
            }*/

            this.lines = sourceCode.Replace("\r", "").Split('\n');

            _modules.Clear();
            _structs.Clear();

            while (HasTokens())
            {
                var firstToken = FetchToken();

                Module module;

                switch (firstToken.value)
                {
                    case "struct":
                        {
                            module = null;

                            var structName = ExpectIdentifier();

                            var fields = new List<StructField>();

                            ExpectToken("{");
                            do
                            {
                                var next = FetchToken();
                                if (next.value == "}")
                                {
                                    break;
                                }

                                Rewind();

                                var fieldName = ExpectIdentifier();
                                ExpectToken(":");

                                var fieldType = ExpectType();
                                ExpectToken(";");

                                fields.Add(new StructField(fieldName, fieldType));
                            } while (true);


                            var structType = (StructVarType)VarType.Find(VarKind.Struct, structName);
                            structType.decl = new StructDeclaration(structName, fields);                            

                            _structs[structName] = structType.decl;
                            break;
                        }

                    case "contract":
                        {
                            var contractName = ExpectIdentifier();

                            module = new Contract(contractName);
                            ExpectToken("{");
                            ParseModule(module);
                            ExpectToken("}");
                            break;
                        }

                    case "script":
                    case "description":
                        {
                            var scriptName = ExpectIdentifier();

                            module = new Script(scriptName, firstToken.value == "description");

                            ExpectToken("{");
                            ParseModule(module);
                            ExpectToken("}");
                            break;
                        }

                    default:
                        throw new CompilerException("Unexpected token: " + firstToken.value);
                }

                if (module != null)
                {
                    module.Compile();
                    _modules.Add(module);
                }
            }

            return _modules.ToArray();
        }

        private void ParseModule(Module module)
        {
            do
            {
                var token = FetchToken();

                switch (token.value)
                {
                    case "}":
                        Rewind();
                        return;

                    case "const":
                        {
                            var constName = ExpectIdentifier();
                            ExpectToken(":");
                            var type = ExpectType();
                            ExpectToken("=");

                            string constVal;

                            switch (type.Kind)
                            {
                                case VarKind.String:
                                    constVal = ExpectString();
                                    break;

                                case VarKind.Number:
                                    constVal = ExpectNumber();
                                    break;

                                case VarKind.Bool:
                                    constVal = ExpectBool();
                                    break;

                                default:
                                    constVal = ExpectIdentifier();
                                    break;
                            }

                            ExpectToken(";");

                            var constDecl = new ConstDeclaration(module.Scope, constName, type, constVal);
                            module.Scope.AddConstant(constDecl);
                            break;
                        }

                    case "global":
                        {                            
                            var varName = ExpectIdentifier();
                            ExpectToken(":");
                            var type = ExpectType();

                            VarDeclaration varDecl;

                            switch (type.Kind)
                            {
                                case VarKind.Storage_Map:
                                    {
                                        ExpectToken("<");
                                        var map_key = ExpectType();
                                        ExpectToken(",");
                                        var map_val = ExpectType();
                                        ExpectToken(">");

                                        varDecl = new MapDeclaration(module.Scope, varName, map_key, map_val);
                                        break;
                                    }

                                case VarKind.Storage_List:
                                    {
                                        ExpectToken("<");
                                        var list_val = ExpectType();
                                        ExpectToken(">");

                                        varDecl = new ListDeclaration(module.Scope, varName, list_val);
                                        break;
                                    }

                                case VarKind.Storage_Set:
                                    {
                                        ExpectToken("<");
                                        var set_val = ExpectType();
                                        ExpectToken(">");

                                        varDecl = new SetDeclaration(module.Scope, varName, set_val);
                                        break;
                                    }

                                default:
                                    {
                                        varDecl = new VarDeclaration(module.Scope, varName, type, VarStorage.Global);
                                        break;
                                    }
                            }

                            ExpectToken(";");
                            module.Scope.AddVariable(varDecl);
                            break;
                        }

                    case "import":
                        {
                            var libName = ExpectIdentifier();
                            ExpectToken(";");

                            var libDecl = Contract.LoadLibrary(libName, module.Scope);
                            module.Libraries[libName] = libDecl;

                            break;
                        }

                    case "event":
                        {
                            var contract = module as Contract;
                            if (contract != null)
                            {
                                var eventName = ExpectIdentifier();
                                ExpectToken(":");
                                var eventType = ExpectType();
                                ExpectToken("=");

                                if (eventType.Kind == VarKind.None || eventType.Kind == VarKind.Unknown || eventType.Kind == VarKind.Generic || eventType.Kind == VarKind.Any)
                                {
                                    throw new CompilerException("invalid event type: " + eventType);
                                }

                                var temp = FetchToken();
                                byte[] description;
                                switch (temp.kind)
                                {
                                    case TokenKind.String:
                                        description = EventDeclaration.GenerateScriptFromString(temp.value);
                                        break;

                                    case TokenKind.Bytes:
                                        description = Base16.Decode(temp.value);
                                        break;

                                    case TokenKind.Identifier:
                                        {
                                            var descModule = FindModule(temp.value) as Script;
                                            if (descModule != null)
                                            {
                                                var descSecondType = descModule.Parameters[1].Type;
                                                if (descSecondType != eventType)
                                                {
                                                    throw new CompilerException($"descriptions second parameter has type {descSecondType}, does not match event type: {eventType}");
                                                }

                                                if (descModule.script != null)
                                                {
                                                    description = descModule.script;
                                                }
                                                else
                                                {
                                                    throw new CompilerException($"description module not ready: {temp.value}");
                                                }
                                            }
                                            else
                                            {
                                                throw new CompilerException($"description module not found: {temp.value}");
                                            }
                                            break;
                                        }

                                    default:
                                        throw new CompilerException($"expected valid event description, got {temp.kind} instead");
                                }

                                ExpectToken(";");

                                var value = (byte)((byte)EventKind.Custom + contract.Events.Count);

                                var eventDecl = new EventDeclaration(module.Scope, eventName, value, eventType, description);
                                contract.Events[eventName] = eventDecl;
                            }
                            else
                            {
                                throw new CompilerException("unexpected token: " + token.value);
                            }
                            break;
                        }


                    case "constructor":
                        {
                            var contract = module as Contract;
                            if (contract != null)
                            {
                                var line = this.CurrentLine;
                                var name = "Initialize";
                                var parameters = ParseParameters(module.Scope);
                                var scope = new Scope(module.Scope, name, parameters);

                                if (parameters.Length != 1 || parameters[0].Type.Kind != VarKind.Address)
                                {
                                    throw new CompilerException("constructor must have only one parameter of type address");
                                }

                                var method = contract.AddMethod(line, name, true, MethodKind.Constructor, VarType.Find(VarKind.None), parameters, scope);

                                ExpectToken("{");

                                contract.SetMethodBody(name, ParseCommandBlock(scope, method));
                                ExpectToken("}");
                                break;
                            }
                            else
                            {
                                throw new CompilerException("unexpected token: " + token.value);                                    
                            }

                        }

                    case "public":
                    case "private":
                        {
                            var contract = module as Contract;
                            if (contract != null)
                            {
                                var line = this.CurrentLine;
                                var name = ExpectIdentifier();

                                var parameters = ParseParameters(module.Scope);
                                var scope = new Scope(module.Scope, name, parameters);

                                var returnType = VarType.Find(VarKind.None);

                                var next = FetchToken();
                                if (next.value == ":")
                                {
                                    returnType = ExpectType();
                                }
                                else
                                {
                                    Rewind();
                                }

                                var method = contract.AddMethod(line, name, token.value == "public", MethodKind.Method, returnType, parameters, scope);

                                ExpectToken("{");
                                contract.SetMethodBody(name, ParseCommandBlock(scope, method));
                                ExpectToken("}");

                                contract.library.AddMethod(name, MethodImplementationType.LocalCall, returnType, parameters);
                                break;
                            }
                            else
                            {
                                throw new CompilerException("unexpected token: " + token.value);
                            }

                        }

                    case "task":
                        {
                            var contract = module as Contract;
                            if (contract != null)
                            {
                                var line = this.CurrentLine;
                                var name = ExpectIdentifier();

                                var parameters = ParseParameters(module.Scope);
                                var scope = new Scope(module.Scope, name, parameters);

                                var method = contract.AddMethod(line, name, true, MethodKind.Task, VarType.Find(VarKind.None), parameters, scope);

                                ExpectToken("{");
                                contract.SetMethodBody(name, ParseCommandBlock(scope, method));
                                ExpectToken("}");

                                break;
                            }
                            else
                            {
                                throw new CompilerException("unexpected token: " + token.value);
                            }
                        }

                    case "trigger":
                        {
                            var contract = module as Contract;
                            if (contract != null)
                            {
                                var line = this.CurrentLine;
                                var name = ExpectIdentifier();

                                if (!name.StartsWith("on"))
                                {
                                    name = "on" + name;
                                }

                                var isValid = false;
                                foreach (var allowedName in Compiler.ValidTriggerNames)
                                {
                                    if (allowedName.Equals(name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        name = allowedName;
                                        isValid = true;
                                        break;
                                    }
                                }

                                if (!isValid)
                                {
                                    throw new CompilerException("invalid trigger name:" + name);
                                }

                                var parameters = ParseParameters(module.Scope);
                                var scope = new Scope(module.Scope, name, parameters);

                                var method = contract.AddMethod(line, name, true, MethodKind.Trigger, VarType.Find(VarKind.None), parameters, scope);

                                ExpectToken("{");
                                contract.SetMethodBody(name, ParseCommandBlock(scope, method));
                                ExpectToken("}");

                                break;
                            }
                            else
                            {
                                throw new CompilerException("unexpected token: " + token.value);
                            }
                        }

                    case "code":
                        {
                            var script = module as Script;
                            if (script != null)
                            {
                                var blockName = "main";
                                script.Parameters = ParseParameters(module.Scope);

                                if (script.Hidden) // if is description
                                {
                                    if (script.Parameters.Length != 2)
                                    {
                                        throw new CompilerException("descriptions must have exactly 2 parameters");
                                    }

                                    if (script.Parameters[0].Type.Kind != VarKind.Address)
                                    {
                                        throw new CompilerException("descriptions first parameter must always be of type " + VarKind.Address);
                                    }
                                }

                                var scope = new Scope(module.Scope, blockName, script.Parameters);

                                script.ReturnType = VarType.Find(VarKind.None);

                                var next = FetchToken();
                                if (next.value == ":")
                                {
                                    script.ReturnType = ExpectType();
                                }
                                else
                                {
                                    Rewind();
                                }

                                var method = new MethodInterface(script.library, MethodImplementationType.Custom, blockName, true, MethodKind.Method, script.ReturnType, new MethodParameter[0]);

                                ExpectToken("{");
                                script.main = ParseCommandBlock(scope, method);
                                ExpectToken("}");

                                break;
                            }
                            else
                            {
                                throw new CompilerException("unexpected token: " + token.value);
                            }
                        }


                    default:
                        throw new CompilerException("unexpected token: " + token.value);
                }

            } while (true);
        }

        private MethodParameter[] ParseParameters(Scope scope)
        {
            var list = new List<MethodParameter>();

            ExpectToken("(");

            do
            {
                var token = FetchToken();

                if (token.value == ")")
                {
                    break;                
                }
                else
                {
                    Rewind();
                }

                if (list.Count > 0)
                {
                    ExpectToken(",");
                }

                var name = ExpectIdentifier();
                ExpectToken(":");
                var type = ExpectType();

                list.Add(new MethodParameter(name, type));

            } while (true);


            return list.ToArray();
        }

        private StatementBlock ParseCommandBlock(Scope scope, MethodInterface method)
        {
            var block = new StatementBlock(scope);

            do
            {
                var token = FetchToken();

                switch (token.value)
                {
                    case "}":
                        Rewind();
                        return block;

                    case "emit":
                        {
                            var contract = scope.Root as Contract;
                            if (contract != null)
                            {
                                var eventName = ExpectIdentifier();

                                ExpectToken("(");

                                var addrExpr = ExpectExpression(scope);
                                ExpectToken(",");
                                var valueExpr = ExpectExpression(scope);
                                ExpectToken(")");
                                ExpectToken(";");

                                if (contract.Events.ContainsKey(eventName))
                                {
                                    var evt = contract.Events[eventName];

                                    if (addrExpr.ResultType.Kind != VarKind.Address)
                                    {
                                        throw new CompilerException($"Expected first argument of type {VarKind.Address}, got {addrExpr.ResultType} instead");
                                    }

                                    if (evt.returnType != valueExpr.ResultType)
                                    {
                                        throw new CompilerException($"Expected second argument of type {evt.returnType}, got {valueExpr.ResultType} instead");
                                    }

                                    block.Commands.Add(new EmitStatement(evt, addrExpr, valueExpr));
                                }
                                else
                                {
                                    throw new CompilerException($"Undeclared event: {eventName}");
                                }

                            }
                            else
                            {
                                throw new CompilerException("Emit statments only allowed in contracts");
                            }

                            break;
                        }

                    case "return":
                        {
                            var temp = FetchToken();
                            Rewind();

                            Expression expr;
                            if (token.value != ";")
                            {
                                expr = ExpectExpression(scope);
                            }
                            else
                            {
                                expr = null;
                            }

                            block.Commands.Add(new ReturnStatement(method, expr));
                            ExpectToken(";");
                            break;
                        }

                    case "throw":
                        {
                            var msg = ExpectString();
                            block.Commands.Add(new ThrowStatement(msg));
                            ExpectToken(";");
                            break;
                        }

                    case "asm":
                        {
                            ExpectToken("{");
                            var lines = ExpectAsm();
                            ExpectToken("}");

                            block.Commands.Add(new AsmBlockStatement(lines));
                            break;
                        }

                    case "local":
                        {
                            var varName = ExpectIdentifier();
                            ExpectToken(":");
                            var kind = ExpectType();

                            var next = FetchToken();

                            Expression initExpr;
                            if (next.value == ":=")
                            {
                                initExpr = ExpectExpression(scope);
                            }
                            else
                            {
                                initExpr = null;
                                Rewind();
                            }

                            ExpectToken(";");

                            var varDecl = new VarDeclaration(scope, varName, kind, VarStorage.Local);
                            scope.AddVariable(varDecl);

                            if (initExpr != null)
                            {
                                var initCmd = new AssignStatement();
                                initCmd.variable = varDecl;
                                initCmd.expression = initExpr;
                                block.Commands.Add(initCmd);
                            }

                            break;
                        }

                    case "if":
                        {
                            var ifCommand = new IfStatement(scope);

                            ExpectToken("(");
                            ifCommand.condition = ExpectExpression(scope);

                            if (ifCommand.condition.ResultType.Kind != VarKind.Bool)
                            {
                                throw new CompilerException($"condition must be boolean expression");
                            }

                            ExpectToken(")");

                            ExpectToken("{");

                            ifCommand.body = ParseCommandBlock(ifCommand.Scope, method);

                            ExpectToken("}");

                            var next = FetchToken();

                            if (next.value == "else")
                            {
                                ExpectToken("{");

                                ifCommand.@else = ParseCommandBlock(ifCommand.Scope, method);

                                ExpectToken("}");
                            }
                            else
                            {
                                Rewind();
                            }

                            block.Commands.Add(ifCommand);
                            break;
                        }

                    case "while":
                        {
                            var whileCommand = new WhileStatement(scope);

                            ExpectToken("(");
                            whileCommand.condition = ExpectExpression(scope);

                            if (whileCommand.condition.ResultType.Kind != VarKind.Bool)
                            {
                                throw new CompilerException($"condition must be boolean expression");
                            }

                            ExpectToken(")");

                            ExpectToken("{");

                            whileCommand.body = ParseCommandBlock(whileCommand.Scope, method);

                            ExpectToken("}");

                            block.Commands.Add(whileCommand);
                            break;
                        }

                    case "do":
                        {
                            var whileCommand = new DoWhileStatement(scope);

                            ExpectToken("{");

                            whileCommand.body = ParseCommandBlock(whileCommand.Scope, method);

                            ExpectToken("}");

                            ExpectToken("while");

                            ExpectToken("(");
                            whileCommand.condition = ExpectExpression(scope);

                            if (whileCommand.condition.ResultType.Kind != VarKind.Bool)
                            {
                                throw new CompilerException($"condition must be boolean expression");
                            }

                            ExpectToken(")");
                            ExpectToken(";");

                            block.Commands.Add(whileCommand);
                            break;
                        }
                    default:
                        if (token.kind == TokenKind.Identifier)
                        {
                            var next = FetchToken();

                            if (next.kind == TokenKind.Operator && next.value.EndsWith("="))
                            {
                                var setCommand = new AssignStatement();
                                setCommand.variable = scope.FindVariable(token.value);

                                var expr = ExpectExpression(scope);
                                if (next.value != ":=")
                                {
                                    var str = next.value.Substring(0, next.value.Length - 1);
                                    var op = ParseOperator(str);

                                    if (op == OperatorKind.Unknown)
                                    {
                                        throw new CompilerException("unknown operator: " + next.value);
                                    }

                                    expr = new BinaryExpression(scope, op, new VarExpression(scope, setCommand.variable), expr);
                                }

                                setCommand.expression = expr;

                                if (setCommand.expression.ResultType != setCommand.variable.Type && setCommand.expression.ResultType.Kind != VarKind.Any)
                                {
                                    throw new CompilerException($"expected {setCommand.variable.Type} expression");
                                }

                                block.Commands.Add(setCommand);
                            }
                            else
                            if (next.kind == TokenKind.Selector)
                            {
                                var varDecl = scope.FindVariable(token.value, false);

                                LibraryDeclaration libDecl;

                                if (varDecl != null)
                                {
                                    switch (varDecl.Type.Kind)
                                    {
                                        case VarKind.Storage_Map:
                                            {
                                                var mapDecl = (MapDeclaration)varDecl;
                                                libDecl = scope.Root.FindLibrary("Map");
                                                libDecl = libDecl.PatchMap(mapDecl);
                                                break;
                                            }

                                        case VarKind.Storage_List:
                                            {
                                                var listDecl = (ListDeclaration)varDecl;
                                                libDecl = scope.Root.FindLibrary("List");
                                                libDecl = libDecl.PatchList(listDecl);
                                                break;
                                            }

                                        case VarKind.Storage_Set:
                                            {
                                                var setDecl = (SetDeclaration)varDecl;
                                                libDecl = scope.Root.FindLibrary("Set");
                                                libDecl = libDecl.PatchSet(setDecl);
                                                break;
                                            }

                                        default:
                                            throw new CompilerException($"expected {token.value} to be generic type, but is {varDecl.Type} instead");
                                    }
                                }
                                else
                                {
                                    libDecl = scope.Root.FindLibrary(token.value);
                                }

                                var methodCall = new MethodCallStatement();
                                methodCall.expression = ParseMethodExpression(scope, libDecl, varDecl);

                                block.Commands.Add(methodCall);
                            }
                            else
                            {
                                throw new CompilerException("unexpected token: " + token.value);
                            }

                            ExpectToken(";");
                        }
                        else
                        {
                            throw new CompilerException("unexpected token: " + token.value);
                        }

                        break;
                }
            } while (true);
        }
        
        private Expression ExpectExpression(Scope scope)
        {
            var expr = ParseExpression(scope);
            if (expr == null)
            {
                throw new CompilerException("expected expression");
            }

            var macro = expr as MacroExpression;
            if (macro != null)
            {
                return macro.Unfold(scope);
            }

            return expr;
        }

        private Expression ParseExpressionFromToken(LexerToken first, Scope scope)
        {
            switch (first.kind)
            {
                case TokenKind.Identifier:
                    {
                        var constDecl = scope.FindConstant(first.value, false);
                        if (constDecl != null)
                        {
                            return new ConstExpression(scope, constDecl);
                        }
                        else
                        {
                            var varDecl = scope.FindVariable(first.value, false);
                            if (varDecl != null)
                            {
                                return new VarExpression(scope, varDecl);
                            }

                            var libDecl = scope.Root.FindLibrary(first.value, false);
                            if (libDecl != null)
                            {
                                throw new NotImplementedException();
                            }

                            throw new CompilerException("unknown identifier: " + first.value);
                        }
                    }

                case TokenKind.Number:
                    {
                        return new LiteralExpression(scope, first.value, VarType.Find(VarKind.Number));
                    }

                case TokenKind.String:
                    {
                        return new LiteralExpression(scope, first.value, VarType.Find(VarKind.String));
                    }

                case TokenKind.Bool:
                    {
                        return new LiteralExpression(scope, first.value, VarType.Find(VarKind.Bool));
                    }

                case TokenKind.Address:
                    {
                        return new LiteralExpression(scope, first.value, VarType.Find(VarKind.Address));
                    }

                case TokenKind.Hash:
                    {
                        return new LiteralExpression(scope, first.value, VarType.Find(VarKind.Hash));
                    }

                case TokenKind.Bytes:
                    {
                        return new LiteralExpression(scope, first.value, VarType.Find(VarKind.Bytes));
                    }

                case TokenKind.Macro:
                    {
                        return new MacroExpression(scope, first.value);
                    }

                default:
                    throw new CompilerException($"cannot turn {first.kind} to an expression");
            }
        }

        private OperatorKind ParseOperator(string str)
        {
            switch (str)
            {
                case "<":
                    return OperatorKind.Less;

                case "<=":
                    return OperatorKind.LessOrEqual;

                case ">":
                    return OperatorKind.Greater;

                case ">=":
                    return OperatorKind.GreaterOrEqual;

                case "==":
                    return OperatorKind.Equal;

                case "!=":
                    return OperatorKind.Different;

                case "/":
                    return OperatorKind.Division;

                case "%":
                    return OperatorKind.Modulus;

                case "*":
                    return OperatorKind.Multiplication;

                case "+":
                    return OperatorKind.Addition;

                case "-":
                    return OperatorKind.Subtraction;

                case "<<":
                    return OperatorKind.ShiftLeft;

                case ">>":
                    return OperatorKind.ShiftRight;

                case "|":
                    return OperatorKind.Or;

                case "&":
                    return OperatorKind.And;

                case "^":
                    return OperatorKind.Xor;

                default:
                    return OperatorKind.Unknown;
            }
        }

        private Expression ParseBinaryExpression(Scope scope, LexerToken opToken, Expression leftSide, Expression rightSide)
        {
            if (opToken.kind != TokenKind.Operator)
            {
                throw new CompilerException("expected operator, got " + opToken.kind);
            }

            var op = ParseOperator(opToken.value);

            if (op == OperatorKind.Unknown)
            {
                throw new CompilerException("unknown operator: " + opToken.value);
            }

            if (rightSide.ResultType != leftSide.ResultType)
            {
                if (leftSide.ResultType.Kind == VarKind.String && op == OperatorKind.Addition)
                {
                    rightSide = new CastExpression(scope, VarType.Find(VarKind.String), rightSide);
                }
                else
                {
                    throw new CompilerException($"type mistmatch, {leftSide.ResultType} on left, {rightSide.ResultType} on right");
                }
            }

            if (op == OperatorKind.Different)
            {
                var innerExpr = new BinaryExpression(scope, OperatorKind.Equal, leftSide, rightSide);

                return new NegationExpression(scope, innerExpr);
            }

            return new BinaryExpression(scope, op, leftSide, rightSide);
        }

        private Expression ParseExpression(Scope scope)
        {
            var first = FetchToken();
            var second = FetchToken();

            switch (second.kind)
            {
                case TokenKind.Separator:
                    {
                        Rewind();
                        return ParseExpressionFromToken(first, scope);
                    }

                case TokenKind.Operator:
                    {
                        var leftSide = ParseExpressionFromToken(first, scope);
                        var rightSide = ExpectExpression(scope);
                        return ParseBinaryExpression(scope, second, leftSide, rightSide);
                    }

                case TokenKind.Selector:
                    {
                        var varDecl = scope.FindVariable(first.value, false);

                        LibraryDeclaration libDecl;

                        if (varDecl != null)
                        {
                            // TODO this code is duplicated, copypasted from other method above, refactor this later...
                            switch (varDecl.Type.Kind)
                            {
                                case VarKind.Storage_Map:
                                    {
                                        var mapDecl = (MapDeclaration)varDecl;
                                        libDecl = scope.Root.FindLibrary("Map");
                                        libDecl = libDecl.PatchMap(mapDecl);
                                        break;
                                    }

                                case VarKind.Storage_List:
                                    {
                                        var listDecl = (ListDeclaration)varDecl;
                                        libDecl = scope.Root.FindLibrary("List");
                                        libDecl = libDecl.PatchList(listDecl);
                                        break;
                                    }

                                case VarKind.Storage_Set:
                                    {
                                        var setDecl = (SetDeclaration)varDecl;
                                        libDecl = scope.Root.FindLibrary("Set");
                                        libDecl = libDecl.PatchSet(setDecl);
                                        break;
                                    }

                                default:
                                    throw new CompilerException($"expected {first.value} to be generic type, but is {varDecl.Type} instead");
                            }
                        }
                        else
                        {
                            libDecl = scope.Root.FindLibrary(first.value);
                        }

                        var leftSide = ParseMethodExpression(scope, libDecl, varDecl);

                        second = FetchToken();
                        if (second.kind == TokenKind.Operator)
                        {
                            var rightSide = ExpectExpression(scope);
                            return ParseBinaryExpression(scope, second, leftSide, rightSide);
                        }
                        else
                        {
                            Rewind();
                            return leftSide;
                        }
                    }

                default:
                    if (first.kind == TokenKind.Separator)
                    {
                        Rewind();
                        var leftSide = ExpectExpression(scope);
                        ExpectToken(")");
                        var op = FetchToken();
                        if (op.kind != TokenKind.Operator)
                        {
                            throw new CompilerException($"expected operator, got {op.kind} instead");
                        }
                        var rightSide = ExpectExpression(scope);
                        return ParseBinaryExpression(scope, op, leftSide, rightSide);
                    }
                    break;
            }

            return null;
        }

        private MethodExpression ParseMethodExpression(Scope scope, LibraryDeclaration library, VarDeclaration implicitArg = null)
        {
            var expr = new MethodExpression(scope);

            var methodName = ExpectIdentifier();

            expr.method = library.FindMethod(methodName);
            ExpectToken("(");

            var firstIndex = implicitArg != null ? 1 : 0;

            bool isCallLibrary = expr.method.Library.Name == "Call";

            if (isCallLibrary)
            {
                int i = 0;
                do
                {
                    var next = FetchToken();
                    if (next.value == ")")
                    {
                        break;
                    }
                    else
                    {
                        Rewind();
                    }

                    if (i > firstIndex)
                    {
                        ExpectToken(",");
                    }

                    Expression arg;

                    if (i == 0 && implicitArg != null)
                    {
                        arg = new LiteralExpression(scope, $"\"{implicitArg.Name}\"", VarType.Find(VarKind.String));
                    }
                    else
                    {
                        arg = ExpectExpression(scope);
                    }

                    expr.arguments.Add(arg);

                    i++;
                } while (true);
            }
            else
            {
                var paramCount = expr.method.Parameters.Length;
                for (int i = 0; i < paramCount; i++)
                {
                    if (i > firstIndex)
                    {
                        ExpectToken(",");
                    }

                    Expression arg;

                    if (i == 0 && implicitArg != null)
                    {
                        arg = new LiteralExpression(scope, $"\"{implicitArg.Name}\"", VarType.Find(VarKind.String));
                    }
                    else
                    {
                        arg = ExpectExpression(scope);
                    }

                    expr.arguments.Add(arg);

                    var expectedType = expr.method.Parameters[i].Type;
                    if (arg.ResultType != expectedType && expectedType.Kind != VarKind.Any)
                    {
                        throw new CompilerException($"expected argument of type {expectedType}, got {arg.ResultType} instead");
                    }                   
                }

                ExpectToken(")");
            }

            return expr;
        }

        public static readonly string[] ValidTriggerNames = Enum.GetNames(typeof(AccountTrigger)).Union(Enum.GetNames(typeof(TokenTrigger))).ToArray();

        private const int MaxRegisters = VirtualMachine.DefaultRegisterCount;
        private Node[] registerAllocs = new Node[MaxRegisters];
        private string[] registerAlias = new string[MaxRegisters];

        public Register AllocRegister(CodeGenerator generator, Node node, string alias = null)
        {
            if (alias != null)
            {
                foreach (var entry in registerAlias)
                {
                    if (entry == alias)
                    {
                        throw new Exception("alias already exists: " + alias);
                    }
                }
            }

            int baseRegister = 1;
            for (int i = baseRegister; i < registerAllocs.Length; i++)
            {
                if (registerAllocs[i] == null)
                {
                    registerAllocs[i] = node;
                    registerAlias[i] = alias;

                    string extra = alias != null ? " => " + alias : "";
                    Console.WriteLine(CodeGenerator.Tabs(CodeGenerator.currentScope.Level) + "alloc r" + i + extra);

                    if (alias != null)
                    {
                        generator.AppendLine(node, $"ALIAS r{i} ${alias}");
                    }

                    return new Register(i, alias);
                }
            }

            throw new CompilerException("no more available registers");
        }

        public void DeallocRegister(ref Register register)
        {
            if (register == null)
            {
                return;
            }

            var index = register.Index;

            if (registerAllocs[index] != null)
            {
                var alias = registerAlias[index];

                Console.WriteLine(CodeGenerator.Tabs(CodeGenerator.currentScope.Level) + "dealloc r" + index + " => "+alias);

                registerAllocs[index] = null;
                registerAlias[index] = null;

                register = null;
                return;
            }

            throw new CompilerException("register not allocated");
        }

        public void VerifyRegisters()
        {

            for (int i = 0; i < registerAllocs.Length; i++)
            {
                if (registerAllocs[i] != null)
                {
                    throw new CompilerException($"register r{i} not deallocated");
                }
            }            
        }

    }
}