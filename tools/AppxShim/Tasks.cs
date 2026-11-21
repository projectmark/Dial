#nullable enable
using System;
using Microsoft.Build.Framework;

namespace Microsoft.Build.AppxPackage
{
    public abstract class BaseShimTask : ITask
    {
        public IBuildEngine? BuildEngine { get; set; }
        public ITaskHost? HostObject { get; set; }
        public string? VsTelemetrySession { get; set; }
        public virtual bool Execute() => true;
    }

    public class ExpandPayloadDirectories : BaseShimTask
    {
        public ITaskItem[]? Inputs { get; set; }
        public ITaskItem[]? TargetDirsToExclude { get; set; }
        public ITaskItem[]? TargetFilesToExclude { get; set; }
        [Output] public ITaskItem[] Expanded { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            Expanded = Inputs ?? Array.Empty<ITaskItem>();
            return true;
        }
    }

    public class GetDefaultResourceLanguage : BaseShimTask
    {
        public string? DefaultLanguage { get; set; }
        public ITaskItem[]? SourceAppxManifest { get; set; }
        [Output] public string DefaultResourceLanguage { get; set; } = "en";
        public override bool Execute()
        {
            DefaultResourceLanguage = string.IsNullOrWhiteSpace(DefaultLanguage) ? "en" : DefaultLanguage;
            return true;
        }
    }

    public class GetPackageArchitecture : BaseShimTask
    {
        public string? Platform { get; set; }
        public ITaskItem[]? ProjectArchitecture { get; set; }
        public ITaskItem[]? RecursiveProjectArchitecture { get; set; }
        [Output] public string PackageArchitecture { get; set; } = "x64";
        public override bool Execute()
        {
            PackageArchitecture = string.IsNullOrWhiteSpace(Platform) || Platform == "AnyCPU" ? "x64" : Platform;
            return true;
        }
    }

    public class GetSdkFileFullPath : BaseShimTask
    {
        public string? FileName { get; set; }
        public string? FullFilePath { get; set; }
        public string? FileArchitecture { get; set; }
        public bool RequireExeExtension { get; set; }
        public string? TargetPlatformSdkRootOverride { get; set; }
        [Output] public string FullPath { get; set; } = "";
        public override bool Execute()
        {
            FullPath = FullFilePath ?? "";
            return true;
        }
    }

    public class GetSdkPropertyValue : BaseShimTask
    {
        public string? PropertyName { get; set; }
        [Output] public string PropertyValue { get; set; } = "";
        public override bool Execute()
        {
            PropertyValue = "";
            return true;
        }
    }

    public class RemovePayloadDuplicates : BaseShimTask
    {
        public ITaskItem[]? Inputs { get; set; }
        public string? ProjectName { get; set; }
        public string? Platform { get; set; }
        [Output] public ITaskItem[] Filtered { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            Filtered = Inputs ?? Array.Empty<ITaskItem>();
            return true;
        }
    }

    public class RemoveRedundantXamlFilesFromSdkPayload : BaseShimTask
    {
        public ITaskItem[]? Inputs { get; set; }
        [Output] public ITaskItem[] Filtered { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            Filtered = Inputs ?? Array.Empty<ITaskItem>();
            return true;
        }
    }

    public class ValidateConfiguration : BaseShimTask
    {
        public string? TargetPlatformMinVersion { get; set; }
        public string? TargetPlatformVersion { get; set; }
        public string? ProjectLanguage { get; set; }
        public string? TargetPlatformIdentifier { get; set; }
        public override bool Execute() => true;
    }
}

namespace Microsoft.Build.Packaging.Pri.Tasks
{
    public class ExpandPriContent : Microsoft.Build.AppxPackage.BaseShimTask
    {
        public ITaskItem[]? Inputs { get; set; }
        public string? MakePriExeFullPath { get; set; }
        public string? MakePriExtensionPath { get; set; }
        public string? IntermediateDirectory { get; set; }
        public string? AdditionalMakepriExeParameters { get; set; }
        public bool ExcludeXamlFromLibraryLayoutsWhenXbfIsPresent { get; set; }

        [Output] public ITaskItem[] Expanded { get; set; } = Array.Empty<ITaskItem>();
        [Output] public ITaskItem[] IntermediateFileWrites { get; set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            Expanded = Inputs ?? Array.Empty<ITaskItem>();
            IntermediateFileWrites = Array.Empty<ITaskItem>();
            return true;
        }
    }

    public class CreatePriConfigXmlForSplitting : Microsoft.Build.AppxPackage.BaseShimTask
    {
        [Output] public ITaskItem? OutputFile { get; set; }
    }

    public class CreatePriConfigXmlForMainPackageFileMap : Microsoft.Build.AppxPackage.BaseShimTask
    {
        [Output] public ITaskItem? OutputFile { get; set; }
    }

    public class CreatePriConfigXmlForFullIndex : Microsoft.Build.AppxPackage.BaseShimTask
    {
        [Output] public ITaskItem? OutputFile { get; set; }
    }

    public class CreatePriFilesForPortableLibraries : Microsoft.Build.AppxPackage.BaseShimTask { }

    public class GenerateMainPriConfigurationFile : Microsoft.Build.AppxPackage.BaseShimTask
    {
        [Output] public ITaskItem? OutputFile { get; set; }
    }

    public class GeneratePriConfigurationFiles : Microsoft.Build.AppxPackage.BaseShimTask { }

    public class GenerateProjectPriFile : Microsoft.Build.AppxPackage.BaseShimTask
    {
        [Output] public ITaskItem? OutputFile { get; set; }
    }

    public class RemoveDuplicatePriFiles : Microsoft.Build.AppxPackage.BaseShimTask
    {
        public ITaskItem[]? Inputs { get; set; }
        [Output] public ITaskItem[] Filtered { get; set; } = Array.Empty<ITaskItem>();
        public override bool Execute()
        {
            Filtered = Inputs ?? Array.Empty<ITaskItem>();
            return true;
        }
    }

    public class UpdateMainPackageFileMap : Microsoft.Build.AppxPackage.BaseShimTask { }
}
