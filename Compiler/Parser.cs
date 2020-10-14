﻿using Phantasma.Domain;
using Phantasma.Numerics;
using Phantasma.VM;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Schema;

namespace Phantasma.Tomb.Compiler
{
    public class Parser
    {
        private List<LexerToken> tokens;
        private int tokenIndex = 0;

        private int currentLabel = 0;

        public int CurrentLine { get; private set; }
        public int CurrentColumn { get; private set; }

        private string[] lines;

        public static Parser Instance { get; private set; }

        public Parser()
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

        private VarKind ExpectType()
        {
            var token = FetchToken();

            if (token.kind != TokenKind.Type)
            {
                throw new CompilerException("expected type, got " + token.kind);
            }

            return (VarKind)Enum.Parse(typeof(VarKind), token.value, true);
        }

        public string GetLine(int index)
        {
            if (index <= 0 || index > lines.Length)
            {
                return "";
            }

            return lines[index-1];
        }

        public Contract Parse(string sourceCode)
        {
            this.tokens = Lexer.Process(sourceCode);

            /*foreach (var token in tokens)
            {
                Console.WriteLine(token);
            }*/

            ExpectToken("contract");
            var contractName = ExpectIdentifier();

            this.lines = sourceCode.Replace("\r", "").Split('\n');

            var contractBlock = new Contract(contractName);
            ExpectToken("{");
            ParseContractBlock(contractBlock);
            ExpectToken("}");
            return contractBlock;
        }

        private void ParseContractBlock(Contract contract)
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
                            var kind = ExpectType();
                            ExpectToken("=");

                            string constVal;

                            switch (kind)
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

                            var constDecl = new ConstDeclaration(contract.Scope, constName, kind, constVal);
                            contract.Scope.AddConstant(constDecl);
                            break;
                        }

                    case "global":
                        {                            
                            var varName = ExpectIdentifier();
                            ExpectToken(":");
                            var kind = ExpectType();

                            VarDeclaration varDecl;

                            switch (kind)
                            {
                                case VarKind.Storage_Map:
                                    {
                                        ExpectToken("<");
                                        var map_key = ExpectType();
                                        ExpectToken(",");
                                        var map_val = ExpectType();
                                        ExpectToken(">");

                                        varDecl = new MapDeclaration(contract.Scope, varName, map_key, map_val);
                                        break;
                                    }

                                case VarKind.Storage_List:
                                    {
                                        ExpectToken("<");
                                        var list_val = ExpectType();
                                        ExpectToken(">");

                                        varDecl = new ListDeclaration(contract.Scope, varName, list_val);
                                        break;
                                    }

                                case VarKind.Storage_Set:
                                    {
                                        ExpectToken("<");
                                        var set_val = ExpectType();
                                        ExpectToken(">");

                                        varDecl = new SetDeclaration(contract.Scope, varName, set_val);
                                        break;
                                    }

                                default:
                                    {
                                        varDecl = new VarDeclaration(contract.Scope, varName, kind, VarStorage.Global);
                                        break;
                                    }
                            }

                            ExpectToken(";");
                            contract.Scope.AddVariable(varDecl);
                            break;
                        }

                    case "import":
                        {
                            var libName = ExpectIdentifier();
                            ExpectToken(";");

                            contract.AddLibrary(libName);
                            break;
                        }

                    case "constructor":
                        {
                            var line = this.CurrentLine;
                            var name = "Initialize";
                            var parameters = ParseParameters(contract.Scope);
                            var scope = new Scope(contract.Scope, name, parameters);

                            if (parameters.Length != 1 || parameters[0].Kind != VarKind.Address)
                            {
                                throw new CompilerException("constructor must have only one parameter of type address");
                            }

                            ExpectToken("{");
                            var body = ParseCommandBlock(scope);
                            ExpectToken("}");
                            
                            contract.AddMethod(line, name, MethodKind.Constructor, VarKind.None, parameters, scope, body);
                            break;
                        }

                    case "method":
                        {
                            var line = this.CurrentLine;
                            var name = ExpectIdentifier();

                            var parameters = ParseParameters(contract.Scope);
                            var scope = new Scope(contract.Scope, name, parameters);

                            var returnType = VarKind.None;

                            var next = FetchToken();
                            if (next.value == ":")
                            {
                                returnType = ExpectType();
                            }
                            else
                            {
                                Rewind();
                            }

                            ExpectToken("{");
                            var body = ParseCommandBlock(scope);
                            ExpectToken("}");

                            contract.AddMethod(line, name, MethodKind.Method, returnType, parameters, scope, body);
                            break;
                        }

                    case "task":
                        {
                            var line = this.CurrentLine;
                            var name = ExpectIdentifier();

                            var parameters = ParseParameters(contract.Scope);
                            var scope = new Scope(contract.Scope, name, parameters);

                            ExpectToken("{");
                            var body = ParseCommandBlock(scope);
                            ExpectToken("}");

                            contract.AddMethod(line, name, MethodKind.Task, VarKind.None, parameters, scope, body);
                            break;
                        }

                    case "trigger":
                        {
                            var line = this.CurrentLine;
                            var name = ExpectIdentifier();

                            if (!name.StartsWith("on"))
                            {
                                name = "on" + name;
                            }

                            var isValid = false;
                            foreach (var allowedName in Parser.ValidTriggerNames)
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
                            
                            var parameters = ParseParameters(contract.Scope);
                            var scope = new Scope(contract.Scope, name, parameters);

                            ExpectToken("{");
                            var body = ParseCommandBlock(scope);
                            ExpectToken("}");

                            contract.AddMethod(line, name, MethodKind.Trigger, VarKind.None, parameters, scope, body);

                            break;
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

        private StatementBlock ParseCommandBlock(Scope scope)
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

                    case "return":
                        {
                            block.Commands.Add(new ReturnStatement());
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

                            if (ifCommand.condition.ResultType != VarKind.Bool)
                            {
                                throw new CompilerException($"condition must be boolean expression");
                            }

                            ExpectToken(")");

                            ExpectToken("{");

                            ifCommand.body = ParseCommandBlock(ifCommand.Scope);

                            ExpectToken("}");

                            var next = FetchToken();

                            if (next.value == "else")
                            {
                                ExpectToken("{");

                                ifCommand.@else = ParseCommandBlock(ifCommand.Scope);

                                ExpectToken("}");
                            }
                            else
                            {
                                Rewind();
                            }

                            block.Commands.Add(ifCommand);
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

                                if (setCommand.expression.ResultType != setCommand.variable.Kind)
                                {
                                    throw new CompilerException($"expected {setCommand.variable.Kind} expression");
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
                                    switch (varDecl.Kind)
                                    {
                                        case VarKind.Storage_Map:
                                            {
                                                var mapDecl = (MapDeclaration)varDecl;
                                                libDecl = scope.Root.FindLibrary("Map");
                                                libDecl.PatchMap(mapDecl);
                                                break;
                                            }

                                        case VarKind.Storage_List:
                                            {
                                                var listDecl = (ListDeclaration)varDecl;
                                                libDecl = scope.Root.FindLibrary("List");
                                                libDecl.PatchList(listDecl);
                                                break;
                                            }

                                        case VarKind.Storage_Set:
                                            {
                                                var setDecl = (SetDeclaration)varDecl;
                                                libDecl = scope.Root.FindLibrary("Set");
                                                libDecl.PatchSet(setDecl);
                                                break;
                                            }

                                        default:
                                            throw new CompilerException($"expected {token.value} to be generic type, but is {varDecl.Kind} instead");
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
                        return new LiteralExpression(scope, first.value, VarKind.Number);
                    }

                case TokenKind.String:
                    {
                        return new LiteralExpression(scope, first.value, VarKind.String);
                    }

                case TokenKind.Bool:
                    {
                        return new LiteralExpression(scope, first.value, VarKind.Bool);
                    }

                case TokenKind.Address:
                    {
                        return new LiteralExpression(scope, first.value, VarKind.Address);
                    }

                case TokenKind.Bytes:
                    {
                        return new LiteralExpression(scope, first.value, VarKind.Bytes);
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
                throw new CompilerException($"type mistmatch, {leftSide.ResultType} on left, {rightSide.ResultType} on right");
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
                            switch (varDecl.Kind)
                            {
                                case VarKind.Storage_Map:
                                    {
                                        var mapDecl = (MapDeclaration)varDecl;
                                        libDecl = scope.Root.FindLibrary("Map");
                                        libDecl.PatchMap(mapDecl);
                                        break;
                                    }

                                case VarKind.Storage_List:
                                    {
                                        var listDecl = (ListDeclaration)varDecl;
                                        libDecl = scope.Root.FindLibrary("List");
                                        libDecl.PatchList(listDecl);
                                        break;
                                    }

                                case VarKind.Storage_Set:
                                    {
                                        var setDecl = (SetDeclaration)varDecl;
                                        libDecl = scope.Root.FindLibrary("Set");
                                        libDecl.PatchSet(setDecl);
                                        break;
                                    }

                                default:
                                    throw new CompilerException($"expected {first.value} to be generic type, but is {varDecl.Kind} instead");
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
                    arg = new LiteralExpression(scope, $"\"{implicitArg.Name}\"", VarKind.String);
                }
                else
                {
                    arg = ExpectExpression(scope);
                }
                
                expr.arguments.Add(arg);

                var expectedType = expr.method.Parameters[i].Kind;
                if (arg.ResultType != expectedType)
                {
                    throw new CompilerException($"expected argument of type {expectedType}, got {arg.ResultType} instead");
                }
            }

            ExpectToken(")");

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

        public void DeallocRegister(Register register)
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
