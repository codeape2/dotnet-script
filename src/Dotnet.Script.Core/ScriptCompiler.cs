using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Dotnet.Script.Core.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using Microsoft.DotNet.InternalAbstractions;
using Microsoft.DotNet.ProjectModel;
using Microsoft.Extensions.DependencyModel;
using System.Runtime.InteropServices;

namespace Dotnet.Script.Core
{
    public class ScriptCompiler
    {
        private readonly ScriptLogger _logger;

        protected virtual IEnumerable<Assembly> ReferencedAssemblies => new[]
        {
            typeof(object).GetTypeInfo().Assembly,
            typeof(Enumerable).GetTypeInfo().Assembly
        };

        protected virtual IEnumerable<string> ImportedNamespaces => new[]
        {
            "System",
            "System.IO",
            "System.Collections.Generic",
            "System.Console",
            "System.Diagnostics",
            "System.Dynamic",
            "System.Linq",
            "System.Linq.Expressions",
            "System.Text",
            "System.Threading.Tasks"
        };

        public ScriptCompiler(ScriptLogger logger)
        {
            _logger = logger;
        }

        public virtual ScriptOptions CreateScriptOptions(ScriptContext context)
        {
            var opts = ScriptOptions.Default.AddImports(ImportedNamespaces)
                .AddReferences(ReferencedAssemblies)
                .WithSourceResolver(SourceFileResolver.Default)
                .WithMetadataResolver(ScriptMetadataResolver.Default)
                .WithEmitDebugInformation(context.DebugMode)
                .WithFileEncoding(context.Code.Encoding);

            if (!string.IsNullOrWhiteSpace(context.FilePath))
            {
                opts = opts.WithFilePath(context.FilePath);
            }

            return opts;
        }

        public virtual ScriptCompilationContext<TReturn> CreateCompilationContext<TReturn, THost>(ScriptContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var runtimeContext = ProjectContext.CreateContextForEachTarget(context.WorkingDirectory).First();
            var runtimeIdentitfer = GetRuntimeIdentitifer();

            _logger.Verbose($"Current runtime is '{runtimeIdentitfer}'.");
            _logger.Verbose($"Found runtime context for '{runtimeContext.ProjectFile.ProjectFilePath}'.");

            var projectExporter = runtimeContext.CreateExporter(context.Configuration);

            var runtimeDependencies = new HashSet<string>();
            var projectDependencies = projectExporter.GetDependencies();

            foreach (var projectDependency in projectDependencies)
            {
                var runtimeAssemblyGroups = projectDependency.RuntimeAssemblyGroups;

                foreach (var libraryAsset in runtimeAssemblyGroups.GetDefaultAssets())
                {
                    var runtimeAssemblyPath = libraryAsset.ResolvedPath;
                    _logger.Verbose($"Discovered runtime dependency for '{runtimeAssemblyPath}'");
                    runtimeDependencies.Add(runtimeAssemblyPath);
                }

                foreach (var runtimeAssemblyGroup in runtimeAssemblyGroups)
                {
                    if (!string.IsNullOrWhiteSpace(runtimeAssemblyGroup.Runtime) && runtimeAssemblyGroup.Runtime == runtimeIdentitfer)
                    {
                        foreach (var runtimeAsset in runtimeAssemblyGroups.GetRuntimeAssets(GetRuntimeIdentitifer()))
                        {
                            var runtimeAssetPath = runtimeAsset.ResolvedPath;
                            _logger.Verbose($"Discovered runtime asset dependency ('{runtimeIdentitfer}') for '{runtimeAssetPath}'");
                            runtimeDependencies.Add(runtimeAssetPath);
                        }
                    }
                }
            }

            var opts = CreateScriptOptions(context);

            var runtimeId = RuntimeEnvironment.GetRuntimeIdentifier();
            var inheritedAssemblyNames = DependencyContext.Default.GetRuntimeAssemblyNames(runtimeId).Where(x =>
                x.FullName.StartsWith("system.", StringComparison.OrdinalIgnoreCase) ||
                x.FullName.StartsWith("microsoft.codeanalysis", StringComparison.OrdinalIgnoreCase) ||
                x.FullName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase));

            foreach (var inheritedAssemblyName in inheritedAssemblyNames)
            {
                _logger.Verbose("Adding reference to an inherited dependency => " + inheritedAssemblyName.FullName);
                var assembly = Assembly.Load(inheritedAssemblyName);
                opts = opts.AddReferences(assembly);
            }

            foreach (var runtimeDep in runtimeDependencies)
            {
                _logger.Verbose("Adding reference to a runtime dependency => " + runtimeDep);
                opts = opts.AddReferences(MetadataReference.CreateFromFile(runtimeDep));
            }

            var loader = new InteractiveAssemblyLoader();
            var script = CSharpScript.Create<TReturn>(context.Code.ToString(), opts, typeof(THost), loader);
            var compilation = script.GetCompilation();

            var diagnostics = compilation.GetDiagnostics();
            var orderedDiagnostics = diagnostics.OrderBy((d1, d2) => 
            {
                var severityDiff = (int)d2.Severity - (int)d1.Severity;
                return severityDiff != 0 ? severityDiff : d1.Location.SourceSpan.Start - d2.Location.SourceSpan.Start;
            });

            if (orderedDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                foreach (var diagnostic in orderedDiagnostics)
                {
                    _logger.Log(diagnostic.ToString());
                }

                throw new CompilationErrorException("Script compilation failed due to one or more errors.",
                    orderedDiagnostics.ToImmutableArray());
            }

            return new ScriptCompilationContext<TReturn>(script, context.Code, loader);
        }

        private static string GetRuntimeIdentitifer()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "unix";

            return "win";
        }
    }
}