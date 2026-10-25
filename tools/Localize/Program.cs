using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;
using System.Text;
var root=Path.GetFullPath(args[1]);
var files=Directory.GetFiles(Path.Combine(root,"src"),"*.cs",SearchOption.AllDirectories).Where(p=>!p.Contains("\\obj\\")&&!p.Contains("\\bin\\")&&!p.EndsWith("Localization.cs")).ToArray();
if(args[0]=="extract") {
 var rows=new List<object>();
 foreach(var file in files) foreach(var node in CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes()) {
  var localized=node.Parent is ArgumentSyntax arg && arg.Parent?.Parent is InvocationExpressionSyntax call && (call.Expression.ToString() is "L.T" or "L.F" || call.Expression.ToString().EndsWith(".L.T") || call.Expression.ToString().EndsWith(".L.F")) && call.ArgumentList.Arguments[0]==arg;
  if(!localized && Path.GetFileName(file).StartsWith("ExpansionPanel") && node.Parent is ArgumentSyntax helperArg)
  {
    if(helperArg.Parent?.Parent is InvocationExpressionSyntax helper)
    {var index=helper.ArgumentList.Arguments.IndexOf(helperArg);localized=helper.Expression.ToString() switch {"Label" or "ActionButton"=>index==0,"Input" or "Toggle" or "AddTab"=>index==1,"Translate"=>index==2,_=>false};}
    else if(helperArg.Parent?.Parent is ObjectCreationExpressionSyntax creation && creation.Type.ToString().StartsWith("Choice<"))localized=creation.ArgumentList!.Arguments.IndexOf(helperArg)==1;
  }
  string? value=node switch { LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression)=>lit.Token.ValueText, InterpolatedStringExpressionSyntax ip=>Rewriter.Format(ip), _=>null };
  if(string.IsNullOrWhiteSpace(value))continue;
  rows.Add(new {text=value,localized,file=Path.GetRelativePath(root,file),line=node.GetLocation().GetLineSpan().StartLinePosition.Line+1,kind=node.Kind().ToString(),context=node.Parent?.Kind().ToString()});
 }
 File.WriteAllText(args[2],JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
} else if(args[0]=="syntax") {
 var errors=files.SelectMany(file=>CSharpSyntaxTree.ParseText(File.ReadAllText(file),path:file).GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error)).ToArray();
 foreach(var error in errors)Console.WriteLine(error);
 Environment.ExitCode=errors.Length==0?0:1;
 Console.WriteLine($"Syntax errors: {errors.Length}");
} else {
 var dictionary=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(args[2]))!;
 foreach(var file in files) {
  var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(file)); var updated=new Rewriter(dictionary).Visit(tree.GetRoot())!;
  if(updated.ToFullString()!=tree.GetRoot().ToFullString())File.WriteAllText(file,updated.ToFullString());
 }
}
class Rewriter(Dictionary<string,string> entries):CSharpSyntaxRewriter {
 public static string Format(InterpolatedStringExpressionSyntax node) {
  var b=new StringBuilder(); int i=0;
  foreach(var c in node.Contents) if(c is InterpolatedStringTextSyntax t)b.Append(t.TextToken.ValueText);
  else if(c is InterpolationSyntax part){b.Append('{').Append(i++);if(part.AlignmentClause is {} align)b.Append(',').Append(align.Value.ToString());if(part.FormatClause is {} fmt)b.Append(':').Append(fmt.FormatStringToken.ValueText);b.Append('}');}
  return b.ToString();
 }
 static string Quote(string s)=>SymbolDisplay.FormatLiteral(s,true);
 public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
  if(node.Expression.ToString() is not ("L.T" or "L.F") && !node.Expression.ToString().EndsWith(".L.T") && !node.Expression.ToString().EndsWith(".L.F"))return base.VisitInvocationExpression(node);
  return node.WithArgumentList(node.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(node.ArgumentList.Arguments.Select((a,i)=>i==0?a:(ArgumentSyntax)Visit(a)!))));
 }
 public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node) {
  if(node.IsKind(SyntaxKind.StringLiteralExpression)&&entries.ContainsKey(node.Token.ValueText))
   return SyntaxFactory.ParseExpression("RobloxMultiManager.Core.L.T("+Quote(node.Token.ValueText)+")").WithTriviaFrom(node);
  return base.VisitLiteralExpression(node);
 }
 public override SyntaxNode? VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node) {
  var key=Format(node); if(!entries.ContainsKey(key))return base.VisitInterpolatedStringExpression(node);
  var args=node.Contents.OfType<InterpolationSyntax>().Select(p=>Visit(p.Expression)!.ToString()).ToArray();
  if(args.Length==0)return SyntaxFactory.ParseExpression("RobloxMultiManager.Core.L.T("+Quote(key)+")").WithTriviaFrom(node);
  return SyntaxFactory.ParseExpression("RobloxMultiManager.Core.L.F("+Quote(key)+", "+string.Join(", ",args)+")").WithTriviaFrom(node);
 }
}
