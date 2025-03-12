/*
Install-Package Microsoft.CodeAnalysis.CSharp
Install-Package Microsoft.CodeAnalysis.CSharp.Workspaces  
Install-Package Microsoft.Build.Locator
Install-Package Microsoft.CodeAnalysis.Workspaces.MSBuild

CallChainAnalyzer.exe "C:\path\to\your\solution.sln" "YourController" "YourActionName"
*/

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CallChainAnalyzer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: CallChainAnalyzer.exe <solution-path> <controller-name> <action-name>");
                return;
            }

            string solutionPath = args[0];
            string controllerName = args[1];
            string actionName = args.Length > 2 ? args[2] : null;

            Console.WriteLine($"Analyzing call chain for {controllerName}.{actionName}...");
            
            await AnalyzeCallChain(solutionPath, controllerName, actionName);
        }

        static async Task AnalyzeCallChain(string solutionPath, string controllerName, string actionName)
        {
            // Initialize MSBuild workspace
            var workspace = MSBuildWorkspace.Create();
            
            // Load the solution
            Console.WriteLine($"Loading solution: {solutionPath}");
            var solution = await workspace.OpenSolutionAsync(solutionPath);
            
            // Find the controller
            var controllerClass = await FindControllerClass(solution, controllerName);
            if (controllerClass == null)
            {
                Console.WriteLine($"Controller '{controllerName}' not found.");
                return;
            }

            // Find the action method
            var actionMethod = FindActionMethod(solution, controllerClass, actionName);
            if (actionMethod == null)
            {
                Console.WriteLine($"Action method '{actionName}' not found in controller '{controllerName}'.");
                return;
            }

            // Create a dictionary to track visited methods to prevent infinite recursion
            var visitedMethods = new HashSet<string>();
            
            // Start the recursive analysis
            Console.WriteLine("Call chain:");
            await AnalyzeMethodCallsRecursively(solution, actionMethod, visitedMethods, 0);
            
            Console.WriteLine("Analysis complete.");
        }

        static async Task<INamedTypeSymbol> FindControllerClass(Solution solution, string controllerName)
        {
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null) continue;

                var controllerTypes = compilation.GetSymbolsWithName(s => 
                    s.Equals(controllerName, StringComparison.OrdinalIgnoreCase) || 
                    s.Equals($"{controllerName}Controller", StringComparison.OrdinalIgnoreCase), 
                    SymbolFilter.Type);

                foreach (var type in controllerTypes)
                {
                    if (type is INamedTypeSymbol namedType && 
                        (type.Name.EndsWith("Controller") || 
                         (type.BaseType != null && type.BaseType.Name.Contains("Controller"))))
                    {
                        return namedType;
                    }
                }
            }
            return null;
        }

        static IMethodSymbol FindActionMethod(Solution solution, INamedTypeSymbol controllerClass, string actionName)
        {
            if (string.IsNullOrEmpty(actionName))
            {
                // If no specific action was provided, list all potential actions
                Console.WriteLine($"Available actions in {controllerClass.Name}:");
                foreach (var member in controllerClass.GetMembers())
                {
                    if (member is IMethodSymbol method && 
                        method.DeclaredAccessibility == Accessibility.Public &&
                        !method.IsStatic)
                    {
                        bool isAction = method.GetAttributes().Any(attr => 
                            attr.AttributeClass.Name.Contains("Http") || 
                            attr.AttributeClass.Name.Contains("Route"));
                        
                        if (isAction || IsLikelyActionMethod(method))
                        {
                            Console.WriteLine($"  - {method.Name}");
                        }
                    }
                }
                return null;
            }

            var methods = controllerClass.GetMembers()
                .Where(m => m is IMethodSymbol && 
                           m.Name.Equals(actionName, StringComparison.OrdinalIgnoreCase))
                .Cast<IMethodSymbol>();

            return methods.FirstOrDefault();
        }

        static bool IsLikelyActionMethod(IMethodSymbol method)
        {
            // Heuristic: Public methods returning IActionResult, Task<IActionResult>, etc. are likely action methods
            if (method.DeclaredAccessibility != Accessibility.Public || method.IsStatic)
                return false;

            string returnTypeName = method.ReturnType.Name;
            return returnTypeName.Contains("ActionResult") || 
                   returnTypeName.Contains("IHttpActionResult") ||
                   method.GetAttributes().Any(a => a.AttributeClass.Name.Contains("Action"));
        }

        static async Task AnalyzeMethodCallsRecursively(
            Solution solution, 
            IMethodSymbol method, 
            HashSet<string> visitedMethods, 
            int depth)
        {
            // Create a unique signature for this method to prevent cycles
            string methodSignature = $"{method.ContainingType.Name}.{method.Name}";
            
            // Print the current method with proper indentation
            string indent = new string(' ', depth * 2);
            Console.WriteLine($"{indent}→ {methodSignature}");
            
            // Check if we've already visited this method
            if (visitedMethods.Contains(methodSignature))
            {
                Console.WriteLine($"{indent}  (already analyzed - preventing cycle)");
                return;
            }
            
            // Mark this method as visited
            visitedMethods.Add(methodSignature);
            
            // Get the definition of this method
            var methodDefinition = await SymbolFinder.FindSourceDefinitionAsync(method, solution);
            if (methodDefinition == null)
            {
                Console.WriteLine($"{indent}  (external or generated method - cannot analyze further)");
                return;
            }
            
            // Find references to this method
            var methodRefs = await SymbolFinder.FindReferencesAsync(methodDefinition, solution);
            
            // Get the method's syntax tree
            var refs = await methodDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntaxAsync();
            if (refs == null) return;
            
            var methodNode = refs as MethodDeclarationSyntax;
            if (methodNode == null) return;
            
            // Get the semantic model
            var document = solution.GetDocument(methodNode.SyntaxTree);
            if (document == null) return;
            
            var semanticModel = await document.GetSemanticModelAsync();
            if (semanticModel == null) return;
            
            // Find all method invocations within this method
            var invocations = methodNode.DescendantNodes()
                .OfType<InvocationExpressionSyntax>();
            
            // Also find object creation expressions (for DI scenarios)
            var objectCreations = methodNode.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>();
            
            // Process method invocations
            foreach (var invocation in invocations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                var targetMethod = symbolInfo.Symbol as IMethodSymbol;
                
                if (targetMethod != null)
                {
                    // Skip system methods and basic framework methods
                    if (!ShouldSkipMethod(targetMethod))
                    {
                        await AnalyzeMethodCallsRecursively(solution, targetMethod, visitedMethods, depth + 1);
                    }
                }
            }
            
            // Process object creations for dependency injection analysis
            foreach (var creation in objectCreations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(creation);
                var constructor = symbolInfo.Symbol as IMethodSymbol;
                
                if (constructor != null)
                {
                    // Find the created type's methods that might be called later
                    var createdType = constructor.ContainingType;
                    
                    Console.WriteLine($"{indent}  [Creates {createdType.Name}]");
                    
                    // Optionally, analyze constructor too
                    if (!ShouldSkipMethod(constructor))
                    {
                        await AnalyzeMethodCallsRecursively(solution, constructor, visitedMethods, depth + 1);
                    }
                }
            }
            
            // Look for dependency injection
            await AnalyzeDependencyInjection(solution, methodNode, semanticModel, visitedMethods, depth);
        }
        
        static async Task AnalyzeDependencyInjection(
            Solution solution,
            MethodDeclarationSyntax methodNode,
            SemanticModel semanticModel,
            HashSet<string> visitedMethods,
            int depth)
        {
            string indent = new string(' ', depth * 2);
            
            // Check for private fields that might be injected dependencies
            var classDecl = methodNode.Parent as ClassDeclarationSyntax;
            if (classDecl == null) return;
            
            // Identify private fields and properties
            var fieldDeclarations = classDecl.Members.OfType<FieldDeclarationSyntax>();
            
            foreach (var fieldDecl in fieldDeclarations)
            {
                foreach (var variable in fieldDecl.Declaration.Variables)
                {
                    var fieldSymbol = semanticModel.GetDeclaredSymbol(variable) as IFieldSymbol;
                    if (fieldSymbol == null) continue;
                    
                    // Look for any method that references this field in the analyzed method
                    var fieldAccesses = methodNode.DescendantNodes()
                        .OfType<IdentifierNameSyntax>()
                        .Where(id => semanticModel.GetSymbolInfo(id).Symbol?.Equals(fieldSymbol) == true);
                    
                    if (fieldAccesses.Any())
                    {
                        // This field is used in the method
                        Console.WriteLine($"{indent}  [Uses dependency: {fieldSymbol.Type.Name}]");
                        
                        // Try to find method calls on this dependency
                        var memberAccesses = methodNode.DescendantNodes()
                            .OfType<MemberAccessExpressionSyntax>()
                            .Where(mae => {
                                var expr = mae.Expression as IdentifierNameSyntax;
                                return expr != null && semanticModel.GetSymbolInfo(expr).Symbol?.Equals(fieldSymbol) == true;
                            });
                        
                        foreach (var access in memberAccesses)
                        {
                            // Check if it's part of a method call
                            var parent = access.Parent;
                            if (parent is InvocationExpressionSyntax invocation)
                            {
                                var methodSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                                if (methodSymbol != null && !ShouldSkipMethod(methodSymbol))
                                {
                                    await AnalyzeMethodCallsRecursively(solution, methodSymbol, visitedMethods, depth + 1);
                                }
                            }
                        }
                    }
                }
            }
            
            // Also check for properties
            var propertyDeclarations = classDecl.Members.OfType<PropertyDeclarationSyntax>();
            foreach (var propDecl in propertyDeclarations)
            {
                var propSymbol = semanticModel.GetDeclaredSymbol(propDecl) as IPropertySymbol;
                if (propSymbol == null) continue;
                
                // Look for any method that references this property in the analyzed method
                var propAccesses = methodNode.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Where(id => semanticModel.GetSymbolInfo(id).Symbol?.Equals(propSymbol) == true);
                
                if (propAccesses.Any())
                {
                    // This property is used in the method
                    Console.WriteLine($"{indent}  [Uses dependency property: {propSymbol.Type.Name}]");
                }
            }
        }
        
        static bool ShouldSkipMethod(IMethodSymbol method)
        {
            // Skip system methods, ToString, Equals, etc.
            string containingNamespace = method.ContainingNamespace?.ToString() ?? "";
            
            return containingNamespace.StartsWith("System") || 
                   method.Name == "ToString" ||
                   method.Name == "Equals" ||
                   method.Name == "GetHashCode" ||
                   method.Name == "GetType";
        }
    }
}
