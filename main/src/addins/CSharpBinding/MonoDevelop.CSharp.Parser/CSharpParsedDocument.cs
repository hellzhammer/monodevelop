﻿//
// CSharpParsedDocument.cs
//
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
//
// Copyright (c) 2015 Xamarin Inc. (http://xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using MonoDevelop.Ide.TypeSystem;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using MonoDevelop.Ide.Editor;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MonoDevelop.Core;

namespace MonoDevelop.CSharp.Parser
{
	class CSharpParsedDocument : ParsedDocument
	{
		internal SyntaxTree Unit {
			get;
			set;
		}

		public CSharpParsedDocument (string fileName) : base (fileName)
		{
		}
		

		#region implemented abstract members of ParsedDocument
		public override Task<IReadOnlyList<Comment>> GetCommentsAsync (CancellationToken cancellationToken = default(CancellationToken))
		{
			return Task.FromResult<IReadOnlyList<Comment>> (new Comment[0]);
		}

		WeakReference<IReadOnlyList<Tag>> weakTags;

		public override Task<IReadOnlyList<Tag>> GetTagCommentsAsync (CancellationToken cancellationToken = default(CancellationToken))
		{
			IReadOnlyList<Tag> result;
			if (weakTags == null || !weakTags.TryGetTarget (out result)) {
				var visitor = new SemanticTagVisitor ();
				if (Unit != null)
					visitor.Visit (Unit.GetRoot (cancellationToken));
				result = visitor.Tags;

				var newRef = new WeakReference<IReadOnlyList<Tag>> (result);
				var oldRef = weakTags;
				while (Interlocked.CompareExchange (ref weakTags, newRef, oldRef) == oldRef) {
				}
			}
			return Task.FromResult (result);
		}

		public class SemanticTagVisitor : CSharpSyntaxVisitor
		{
			public List<Tag> Tags =  new List<Tag> ();

			public override void VisitThrowStatement (Microsoft.CodeAnalysis.CSharp.Syntax.ThrowStatementSyntax node)
			{
				base.VisitThrowStatement (node);
				var createExpression = node.Expression as ObjectCreationExpressionSyntax;
				if (createExpression == null)
					return;
				var st = createExpression.Type.ToString ();
				if (st == "NotImplementedException" || st == "System.NotImplementedException") {
					var loc = node.GetLocation ().GetLineSpan ();
					if (createExpression.ArgumentList.Arguments.Count > 0) {
						Tags.Add (new Tag ("High", GettextCatalog.GetString ("NotImplementedException({0}) thrown.", createExpression.ArgumentList.Arguments.First ().ToString ()), new DocumentRegion (loc.StartLinePosition, loc.EndLinePosition)));
					} else {
						Tags.Add (new Tag ("High", GettextCatalog.GetString ("NotImplementedException thrown."), new DocumentRegion (loc.StartLinePosition, loc.EndLinePosition)));
					}
				}
			}
		}

		WeakReference<IReadOnlyList<FoldingRegion>> weakFoldings;

		public override Task<IReadOnlyList<FoldingRegion>> GetFoldingsAsync (CancellationToken cancellationToken = default(CancellationToken))
		{
			IReadOnlyList<FoldingRegion> result;
			if (weakFoldings == null || !weakFoldings.TryGetTarget (out result)) {

				result = GenerateFoldings (cancellationToken).ToList ();

				var newRef = new WeakReference<IReadOnlyList<FoldingRegion>> (result);
				var oldRef = weakFoldings;
				while (Interlocked.CompareExchange (ref weakFoldings, newRef, oldRef) == oldRef) {
				}
			}

			return Task.FromResult (result);
		}

		IEnumerable<FoldingRegion> GenerateFoldings (CancellationToken cancellationToken)
		{
			foreach (var fold in GetCommentsAsync().Result.ToFolds ())
				yield return fold;

			var visitor = new FoldingVisitor ();
			if (Unit != null)
				visitor.Visit (Unit.GetRoot (cancellationToken));
			foreach (var fold in visitor.Foldings)
				yield return fold;
		}

		class FoldingVisitor : CSharpSyntaxWalker
		{
			public readonly List<FoldingRegion> Foldings = new List<FoldingRegion> ();

			void AddUsings (SyntaxNode parent)
			{
				SyntaxNode firstChild = null, lastChild = null;
				foreach (var child in parent.ChildNodes ()) {
					if (child is UsingDirectiveSyntax) {
						if (firstChild == null) {
							firstChild = child;
						}
						lastChild = child;
						continue;
					}
					if (firstChild != null)
						break;
				}

				if (firstChild != null && firstChild != lastChild) {
					var first = firstChild.GetLocation ().GetLineSpan ();
					var last = lastChild.GetLocation ().GetLineSpan ();

					Foldings.Add (new FoldingRegion (new DocumentRegion (first.StartLinePosition, last.EndLinePosition), FoldType.Undefined));
				}
			}

			public override void VisitCompilationUnit (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax node)
			{
				AddUsings (node);
				base.VisitCompilationUnit (node);
			}

			void AddFolding (SyntaxToken openBrace, SyntaxToken closeBrace)
			{
				openBrace = openBrace.GetPreviousToken (false, false, true, true);

				var first = openBrace.GetLocation ().GetLineSpan ();
				var last = closeBrace.GetLocation ().GetLineSpan ();

				if (first.EndLinePosition.Line != last.EndLinePosition.Line)
					Foldings.Add (new FoldingRegion (new DocumentRegion (first.EndLinePosition, last.EndLinePosition), FoldType.Undefined));
			}


			public override void VisitNamespaceDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax node)
			{
				AddUsings (node);
				AddFolding (node.OpenBraceToken, node.CloseBraceToken);
				base.VisitNamespaceDeclaration (node);
			}

			public override void VisitClassDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax node)
			{
				AddFolding (node.OpenBraceToken, node.CloseBraceToken);
				base.VisitClassDeclaration (node);
			}

			public override void VisitStructDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax node)
			{
				AddFolding (node.OpenBraceToken, node.CloseBraceToken);
				base.VisitStructDeclaration (node);
			}

			public override void VisitInterfaceDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax node)
			{
				AddFolding (node.OpenBraceToken, node.CloseBraceToken);
				base.VisitInterfaceDeclaration (node);
			}

			public override void VisitEnumDeclaration (Microsoft.CodeAnalysis.CSharp.Syntax.EnumDeclarationSyntax node)
			{
				AddFolding (node.OpenBraceToken, node.CloseBraceToken);
				base.VisitEnumDeclaration (node);
			}

			public override void VisitBlock (Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax node)
			{
				AddFolding (node.OpenBraceToken, node.CloseBraceToken);
				base.VisitBlock (node);
			}
		}

		static readonly IReadOnlyList<Error> emptyErrors = new Error[0];
		WeakReference<IReadOnlyList<Error>> weakErrors;

		public override Task<IReadOnlyList<Error>> GetErrorsAsync (CancellationToken cancellationToken = default(CancellationToken))
		{
			var model = GetAst<SemanticModel> ();
			if (model == null)
				return Task.FromResult (emptyErrors);
			
			IReadOnlyList<Error> result;
			if (weakErrors == null || !weakErrors.TryGetTarget (out result)) {
				result = model
					.GetDiagnostics (null, cancellationToken)
					.Where (diag => diag.Severity == DiagnosticSeverity.Error || diag.Severity == DiagnosticSeverity.Warning)
					.Select ((Diagnostic diag) => new Error (GetErrorType (diag.Severity), diag.GetMessage (), GetRegion (diag)))
					.ToList ();
				var newRef = new WeakReference<IReadOnlyList<Error>> (result);
				var oldRef = weakErrors;
				while (Interlocked.CompareExchange (ref weakErrors, newRef, oldRef) == oldRef) {
				}
			}
			return Task.FromResult (result);
		}

		static DocumentRegion GetRegion (Diagnostic diagnostic)
		{
			var lineSpan = diagnostic.Location.GetLineSpan ();
			return new DocumentRegion (lineSpan.StartLinePosition, lineSpan.EndLinePosition);
		}

		static ErrorType GetErrorType (DiagnosticSeverity severity)
		{
			switch (severity) {
			case DiagnosticSeverity.Error:
				return ErrorType.Error;
			case DiagnosticSeverity.Warning:
				return ErrorType.Warning;
			}
			return ErrorType.Unknown;
		}

		#endregion
	}
}