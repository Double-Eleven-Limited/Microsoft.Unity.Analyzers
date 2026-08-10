using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ProfilerSampleAnalyzer
{
	[DiagnosticAnalyzer(LanguageNames.CSharp)]
	public sealed class ProfilerSampleDiagnosticAnalyzer : DiagnosticAnalyzer
	{
		private static readonly DiagnosticDescriptor UnclosedSampleRule = new DiagnosticDescriptor(
			"PSA001",
			"Profiler sample is left open",
			"{0} exits with '{1}' still open. Add Profiler.EndSample() on every path, or wrap the body in try/finally or ProfilerMarker.Auto().",
			"Correctness",
			DiagnosticSeverity.Warning,
			isEnabledByDefault: true);

		private static readonly DiagnosticDescriptor UnmatchedEndSampleRule = new DiagnosticDescriptor(
			"PSA002",
			"Profiler EndSample has no matching BeginSample",
			"{0} calls EndSample() without a matching open BeginSample()",
			"Correctness",
			DiagnosticSeverity.Warning,
			isEnabledByDefault: true);

		private static readonly DiagnosticDescriptor UnsafeExitRule = new DiagnosticDescriptor(
			"PSA003",
			"Profiler sample can be left open by early exit",
			"{0} can leave '{1}' open via '{2}'. Use try/finally or ProfilerMarker.Auto() for this sample.",
			"Correctness",
			DiagnosticSeverity.Warning,
			isEnabledByDefault: true);

		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
			ImmutableArray.Create(UnclosedSampleRule, UnmatchedEndSampleRule, UnsafeExitRule);

		public override void Initialize(AnalysisContext context)
		{
			context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
			context.EnableConcurrentExecution();

			context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
			context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.ConstructorDeclaration);
			context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.DestructorDeclaration);
			context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.OperatorDeclaration);
			context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.ConversionOperatorDeclaration);
			context.RegisterSyntaxNodeAction(AnalyzeAccessor, SyntaxKind.GetAccessorDeclaration);
			context.RegisterSyntaxNodeAction(AnalyzeAccessor, SyntaxKind.SetAccessorDeclaration);
			context.RegisterSyntaxNodeAction(AnalyzeAccessor, SyntaxKind.AddAccessorDeclaration);
			context.RegisterSyntaxNodeAction(AnalyzeAccessor, SyntaxKind.RemoveAccessorDeclaration);
			context.RegisterSyntaxNodeAction(AnalyzeLocalFunction, SyntaxKind.LocalFunctionStatement);
			context.RegisterSyntaxNodeAction(AnalyzeAnonymousFunction, SyntaxKind.ParenthesizedLambdaExpression);
			context.RegisterSyntaxNodeAction(AnalyzeAnonymousFunction, SyntaxKind.SimpleLambdaExpression);
			context.RegisterSyntaxNodeAction(AnalyzeAnonymousFunction, SyntaxKind.AnonymousMethodExpression);
		}

		private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
		{
			var method = (BaseMethodDeclarationSyntax)context.Node;
			AnalyzeBody(context, method.Body, DescribeMethod(method));
		}

		private static void AnalyzeAccessor(SyntaxNodeAnalysisContext context)
		{
			var accessor = (AccessorDeclarationSyntax)context.Node;
			AnalyzeBody(context, accessor.Body, DescribeAccessor(accessor));
		}

		private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext context)
		{
			var localFunction = (LocalFunctionStatementSyntax)context.Node;
			AnalyzeBody(context, localFunction.Body, "local function '" + localFunction.Identifier.ValueText + "'");
		}

		private static void AnalyzeAnonymousFunction(SyntaxNodeAnalysisContext context)
		{
			var anonymousFunction = (AnonymousFunctionExpressionSyntax)context.Node;
			AnalyzeBody(context, anonymousFunction.Body as BlockSyntax, "anonymous function");
		}

		private static void AnalyzeBody(SyntaxNodeAnalysisContext context, BlockSyntax? body, string description)
		{
			if (body == null)
			{
				return;
			}

			var analyzer = new BodyAnalyzer(context, description);
			if (analyzer.VisitBlock(body))
			{
				analyzer.ReportUnclosedSamplesAtEnd();
			}
		}

		private static string DescribeMethod(BaseMethodDeclarationSyntax method)
		{
			var methodDeclaration = method as MethodDeclarationSyntax;
			if (methodDeclaration != null)
			{
				return "method '" + methodDeclaration.Identifier.ValueText + "'";
			}

			var constructor = method as ConstructorDeclarationSyntax;
			if (constructor != null)
			{
				return "constructor '" + constructor.Identifier.ValueText + "'";
			}

			var destructor = method as DestructorDeclarationSyntax;
			if (destructor != null)
			{
				return "destructor '" + destructor.Identifier.ValueText + "'";
			}

			var conversionOperator = method as ConversionOperatorDeclarationSyntax;
			if (conversionOperator != null)
			{
				return "operator '" + conversionOperator.OperatorKeyword.ValueText + "'";
			}

			var operatorDeclaration = method as OperatorDeclarationSyntax;
			if (operatorDeclaration != null)
			{
				return "operator '" + operatorDeclaration.OperatorToken.ValueText + "'";
			}

			return "method";
		}

		private static string DescribeAccessor(AccessorDeclarationSyntax accessor)
		{
			var property = accessor.Parent != null ? accessor.Parent.Parent as PropertyDeclarationSyntax : null;
			if (property != null)
			{
				return accessor.Keyword.ValueText + " accessor '" + property.Identifier.ValueText + "'";
			}

			if (accessor.Parent != null && accessor.Parent.Parent is IndexerDeclarationSyntax)
			{
				return accessor.Keyword.ValueText + " accessor 'this[]'";
			}

			var eventDeclaration = accessor.Parent != null ? accessor.Parent.Parent as EventDeclarationSyntax : null;
			if (eventDeclaration != null)
			{
				return accessor.Keyword.ValueText + " accessor '" + eventDeclaration.Identifier.ValueText + "'";
			}

			return accessor.Keyword.ValueText + " accessor";
		}

		private sealed class BodyAnalyzer
		{
			private readonly SyntaxNodeAnalysisContext context;
			private readonly string description;
			private readonly Stack<ProfilerSample> openSamples = new Stack<ProfilerSample>();
			private readonly Stack<int> breakSampleCounts = new Stack<int>();
			private readonly Stack<int> continueSampleCounts = new Stack<int>();
			private int finallyEndSampleBudget;

			public BodyAnalyzer(SyntaxNodeAnalysisContext context, string description)
			{
				this.context = context;
				this.description = description;
			}

			public bool VisitBlock(BlockSyntax block)
			{
				foreach (var statement in block.Statements)
				{
					if (!VisitStatement(statement))
					{
						return false;
					}
				}

				return true;
			}

			public void ReportUnclosedSamplesAtEnd()
			{
				foreach (var sample in openSamples)
				{
					Report(UnclosedSampleRule, sample.Invocation, description, sample.Name);
				}
			}

			private bool VisitStatement(StatementSyntax statement)
			{
				if (context.CancellationToken.IsCancellationRequested)
				{
					return true;
				}

				var block = statement as BlockSyntax;
				if (block != null)
				{
					return VisitBlock(block);
				}

				if (statement is LocalFunctionStatementSyntax)
				{
					return true;
				}

				var expressionStatement = statement as ExpressionStatementSyntax;
				if (expressionStatement != null)
				{
					VisitExpressionStatement(expressionStatement);
					return true;
				}

				if (statement is ReturnStatementSyntax || statement is ThrowStatementSyntax || IsYieldBreakStatement(statement))
				{
					ReportUnsafeExit(statement, 0);
					return false;
				}

				if (statement is BreakStatementSyntax)
				{
					ReportUnsafeExit(statement, breakSampleCounts.Count > 0 ? breakSampleCounts.Peek() : 0);
					return false;
				}

				if (statement is ContinueStatementSyntax)
				{
					ReportUnsafeExit(statement, continueSampleCounts.Count > 0 ? continueSampleCounts.Peek() : 0);
					return false;
				}

				var ifStatement = statement as IfStatementSyntax;
				if (ifStatement != null)
				{
					var beforeIf = SnapshotOpenSamples();
					var thenCompletes = VisitStatement(ifStatement.Statement);
					var afterThen = SnapshotOpenSamples();

					if (ifStatement.Else == null)
					{
						RestoreOpenSamples(thenCompletes ? afterThen : beforeIf);
						return true;
					}

					RestoreOpenSamples(beforeIf);
					var elseCompletes = VisitStatement(ifStatement.Else.Statement);
					var afterElse = SnapshotOpenSamples();

					if (thenCompletes && !elseCompletes)
					{
						RestoreOpenSamples(afterThen);
						return true;
					}

					if (!thenCompletes && elseCompletes)
					{
						RestoreOpenSamples(afterElse);
						return true;
					}

					if (thenCompletes)
					{
						RestoreOpenSamples(afterElse);
						return true;
					}

					RestoreOpenSamples(beforeIf);
					return false;
				}

				var forStatement = statement as ForStatementSyntax;
				if (forStatement != null)
				{
					VisitLoopBody(forStatement.Statement);
					return true;
				}

				var forEachStatement = statement as ForEachStatementSyntax;
				if (forEachStatement != null)
				{
					VisitLoopBody(forEachStatement.Statement);
					return true;
				}

				var forEachVariableStatement = statement as ForEachVariableStatementSyntax;
				if (forEachVariableStatement != null)
				{
					VisitLoopBody(forEachVariableStatement.Statement);
					return true;
				}

				var whileStatement = statement as WhileStatementSyntax;
				if (whileStatement != null)
				{
					VisitLoopBody(whileStatement.Statement);
					return true;
				}

				var doStatement = statement as DoStatementSyntax;
				if (doStatement != null)
				{
					VisitLoopBody(doStatement.Statement);
					return true;
				}

				var usingStatement = statement as UsingStatementSyntax;
				if (usingStatement != null)
				{
					return VisitStatement(usingStatement.Statement);
				}

				var lockStatement = statement as LockStatementSyntax;
				if (lockStatement != null)
				{
					return VisitStatement(lockStatement.Statement);
				}

				var checkedStatement = statement as CheckedStatementSyntax;
				if (checkedStatement != null)
				{
					return VisitBlock(checkedStatement.Block);
				}

				var unsafeStatement = statement as UnsafeStatementSyntax;
				if (unsafeStatement != null)
				{
					return VisitBlock(unsafeStatement.Block);
				}

				var fixedStatement = statement as FixedStatementSyntax;
				if (fixedStatement != null)
				{
					return VisitStatement(fixedStatement.Statement);
				}

				var tryStatement = statement as TryStatementSyntax;
				if (tryStatement != null)
				{
					VisitTryStatement(tryStatement);
					return true;
				}

				var switchStatement = statement as SwitchStatementSyntax;
				if (switchStatement != null)
				{
					breakSampleCounts.Push(openSamples.Count);
					foreach (var section in switchStatement.Sections)
					{
						foreach (var switchStatementStatement in section.Statements)
						{
							VisitStatement(switchStatementStatement);
						}
					}
					breakSampleCounts.Pop();
					return true;
				}

				foreach (var invocation in statement.DescendantNodes(ShouldDescendIntoChildNode).OfType<InvocationExpressionSyntax>())
				{
					VisitInvocation(invocation);
				}

				return true;
			}

			private void VisitLoopBody(StatementSyntax statement)
			{
				var beforeLoopBody = SnapshotOpenSamples();
				var protectedSampleCount = openSamples.Count;

				breakSampleCounts.Push(protectedSampleCount);
				continueSampleCounts.Push(protectedSampleCount);
				VisitStatement(statement);
				continueSampleCounts.Pop();
				breakSampleCounts.Pop();

				RestoreOpenSamples(beforeLoopBody);
			}

			private static bool IsYieldBreakStatement(StatementSyntax statement)
			{
				var yieldStatement = statement as YieldStatementSyntax;
				return yieldStatement != null && yieldStatement.ReturnOrBreakKeyword.IsKind(SyntaxKind.BreakKeyword);
			}

			private void ReportUnsafeExit(StatementSyntax statement, int protectedSampleCount)
			{
				var protectedSamples = protectedSampleCount + finallyEndSampleBudget;
				var unprotectedSampleCount = Math.Max(0, openSamples.Count - protectedSamples);
				if (unprotectedSampleCount == 0)
				{
					return;
				}

				var sample = openSamples.Take(unprotectedSampleCount).First();
				Report(UnsafeExitRule, statement, description, sample.Name, statement.GetFirstToken().ValueText);
			}

			private static bool ShouldDescendIntoChildNode(SyntaxNode node)
			{
				return !(node is AnonymousFunctionExpressionSyntax) && !(node is LocalFunctionStatementSyntax);
			}

			private void VisitTryStatement(TryStatementSyntax tryStatement)
			{
				var previousFinallyBudget = finallyEndSampleBudget;
				finallyEndSampleBudget += CountEndSamples(tryStatement.Finally != null ? tryStatement.Finally.Block : null);

				VisitBlock(tryStatement.Block);

				foreach (var catchClause in tryStatement.Catches)
				{
					VisitBlock(catchClause.Block);
				}

				finallyEndSampleBudget = previousFinallyBudget;

				if (tryStatement.Finally != null)
				{
					VisitBlock(tryStatement.Finally.Block);
				}
			}

			private static int CountEndSamples(BlockSyntax? block)
			{
				if (block == null)
				{
					return 0;
				}

				return block
					.DescendantNodes(ShouldDescendIntoChildNode)
					.OfType<InvocationExpressionSyntax>()
					.Count(IsEndSampleInvocation);
			}

			private void VisitExpressionStatement(ExpressionStatementSyntax expressionStatement)
			{
				var invocation = expressionStatement.Expression as InvocationExpressionSyntax;
				if (invocation != null)
				{
					VisitInvocation(invocation);
					return;
				}

				foreach (var descendantInvocation in expressionStatement.DescendantNodes(ShouldDescendIntoChildNode).OfType<InvocationExpressionSyntax>())
				{
					VisitInvocation(descendantInvocation);
				}
			}

			private void VisitInvocation(InvocationExpressionSyntax invocation)
			{
				if (IsBeginSampleInvocation(invocation))
				{
					openSamples.Push(new ProfilerSample(GetSampleName(invocation), invocation));
					return;
				}

				if (!IsEndSampleInvocation(invocation))
				{
					return;
				}

				if (openSamples.Count == 0)
				{
					Report(UnmatchedEndSampleRule, invocation, description);
					return;
				}

				openSamples.Pop();
			}

			private List<ProfilerSample> SnapshotOpenSamples()
			{
				return openSamples.ToList();
			}

			private void RestoreOpenSamples(List<ProfilerSample> samples)
			{
				openSamples.Clear();
				for (int i = samples.Count - 1; i >= 0; --i)
				{
					openSamples.Push(samples[i]);
				}
			}

			private static bool IsBeginSampleInvocation(InvocationExpressionSyntax invocation)
			{
				return IsProfilerInvocation(invocation, "BeginSample")
					|| IsProfilerInvocation(invocation, "BeginProfilerSample");
			}

			private static bool IsEndSampleInvocation(InvocationExpressionSyntax invocation)
			{
				return IsProfilerInvocation(invocation, "EndSample")
					|| IsProfilerInvocation(invocation, "EndProfilerSample");
			}

			private static bool IsProfilerInvocation(InvocationExpressionSyntax invocation, string methodName)
			{
				var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
				if (memberAccess != null)
				{
					return IsRelevantMemberAccess(memberAccess, methodName);
				}

				var identifierName = invocation.Expression as IdentifierNameSyntax;
				return identifierName != null && string.Equals(identifierName.Identifier.ValueText, methodName, StringComparison.Ordinal);
			}

			private static bool IsRelevantMemberAccess(MemberAccessExpressionSyntax memberAccess, string methodName)
			{
				if (!string.Equals(memberAccess.Name.Identifier.ValueText, methodName, StringComparison.Ordinal))
				{
					return false;
				}

				var receiver = memberAccess.Expression.ToString();
				return receiver.EndsWith("Profiler", StringComparison.Ordinal)
					|| receiver.EndsWith("UnityEngine.Profiling.Profiler", StringComparison.Ordinal)
					|| receiver.EndsWith("TimeWarning", StringComparison.Ordinal);
			}

			private static string GetSampleName(InvocationExpressionSyntax invocation)
			{
				var firstArgument = invocation.ArgumentList.Arguments.FirstOrDefault();
				var literal = firstArgument != null ? firstArgument.Expression as LiteralExpressionSyntax : null;
				if (literal != null && literal.IsKind(SyntaxKind.StringLiteralExpression))
				{
					return literal.Token.ValueText;
				}

				return invocation.Expression.ToString();
			}

			private void Report(DiagnosticDescriptor descriptor, SyntaxNode node, params object[] messageArgs)
			{
				context.ReportDiagnostic(Diagnostic.Create(descriptor, node.GetLocation(), messageArgs));
			}
		}

		private sealed class ProfilerSample
		{
			public ProfilerSample(string name, InvocationExpressionSyntax invocation)
			{
				Name = name;
				Invocation = invocation;
			}

			public string Name { get; }
			public InvocationExpressionSyntax Invocation { get; }
		}
	}
}
