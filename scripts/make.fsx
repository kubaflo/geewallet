﻿#!/usr/bin/env fsharpi

open System
open System.IO
open System.Linq
open System.Diagnostics

open System.Text
open System.Text.RegularExpressions
#r "System.Core.dll"
open System.Xml
#r "System.Xml.Linq.dll"
open System.Xml.Linq
open System.Xml.XPath

#r "System.Configuration"
open System.Configuration
#load "InfraLib/Misc.fs"
#load "InfraLib/Process.fs"
#load "InfraLib/Git.fs"
open FSX.Infrastructure
open Process

let UNIX_NAME = "geewallet"
let CONSOLE_FRONTEND = "GWallet.Frontend.Console"
let GTK_FRONTEND = "GWallet.Frontend.XF.Gtk"
let DEFAULT_SOLUTION_FILE = "gwallet.core.sln"
let LINUX_SOLUTION_FILE = "gwallet.linux.sln"
let MAC_SOLUTION_FILE = "gwallet.mac.sln"
let BACKEND = "GWallet.Backend"

type Frontend =
    | Console
    | Gtk
    member self.GetProjectName() =
        match self with
        | Console -> CONSOLE_FRONTEND
        | Gtk -> GTK_FRONTEND
    member self.GetExecutableName() =
        match self with
        | Console -> CONSOLE_FRONTEND
        | Gtk -> UNIX_NAME
    override self.ToString() =
        sprintf "%A" self

type BinaryConfig =
    | Debug
    | Release
    override self.ToString() =
        sprintf "%A" self

module MapHelper =
    let GetKeysOfMap (map: Map<'K,'V>): seq<'K> =
        map |> Map.toSeq |> Seq.map fst

    let MergeIntoMap<'K,'V when 'K: comparison> (from: seq<'K*'V>): Map<'K,seq<'V>> =
        let keys = from.Select (fun (k, v) -> k)
        let keyValuePairs =
            seq {
                for key in keys do
                    let valsForKey = (from.Where (fun (k, v) -> key = k)).Select (fun (k, v) -> v) |> seq
                    yield key,valsForKey
            }
        keyValuePairs |> Map.ofSeq

[<StructuralEquality; StructuralComparison>]
type private PackageInfo =
    {
        PackageId: string
        PackageVersion: string
        ReqReinstall: Option<bool>
    }

type private DependencyHolder =
    { Name: string }

[<CustomComparison; CustomEquality>]
type private ComparableFileInfo =
    {
        File: FileInfo
    }
    member self.DependencyHolderName: DependencyHolder =
        if self.File.FullName.ToLower().EndsWith ".nuspec" then
            { Name = self.File.Name }
        else
            { Name = self.File.Directory.Name + "/" }

    interface IComparable with
        member this.CompareTo obj =
            match obj with
            | null -> this.File.FullName.CompareTo null
            | :? ComparableFileInfo as other -> this.File.FullName.CompareTo other.File.FullName
            | _ -> invalidArg "obj" "not a ComparableFileInfo"
    override this.Equals obj =
        match obj with
        | :? ComparableFileInfo as other ->
            this.File.FullName.Equals other.File.FullName
        | _ -> false
    override this.GetHashCode () =
        this.File.FullName.GetHashCode ()


let rec private GatherTarget (args: string list, targetSet: Option<string>): Option<string> =
    match args with
    | [] -> targetSet
    | head::tail ->
        if (targetSet.IsSome) then
            failwith "only one target can be passed to make"
        GatherTarget (tail, Some (head))

let scriptsDir = __SOURCE_DIRECTORY__ |> DirectoryInfo
let rootDir = Path.Combine(scriptsDir.FullName, "..") |> DirectoryInfo

let buildConfigFileName = "build.config"
let buildConfigContents =
    let buildConfig = FileInfo (Path.Combine (scriptsDir.FullName, buildConfigFileName))
    if not (buildConfig.Exists) then
        let configureLaunch =
            match Misc.GuessPlatform() with
            | Misc.Platform.Windows -> ".\\configure.bat"
            | _ -> "./configure.sh"
        Console.Error.WriteLine (sprintf "ERROR: configure hasn't been run yet, run %s first"
                                         configureLaunch)
        Environment.Exit 1

    let skipBlankLines line = not <| String.IsNullOrWhiteSpace line
    let splitLineIntoKeyValueTuple (line:string) =
        let pair = line.Split([|'='|], StringSplitOptions.RemoveEmptyEntries)
        if pair.Length <> 2 then
            failwithf "All lines in %s must conform to format:\n\tkey=value"
                      buildConfigFileName
        pair.[0], pair.[1]

    let buildConfigContents =
        File.ReadAllLines buildConfig.FullName
        |> Array.filter skipBlankLines
        |> Array.map splitLineIntoKeyValueTuple
        |> Map.ofArray
    buildConfigContents

let GetOrExplain key map =
    match map |> Map.tryFind key with
    | Some k -> k
    | None   -> failwithf "No entry exists in %s with a key '%s'."
                          buildConfigFileName key

let prefix = buildConfigContents |> GetOrExplain "Prefix"
let libPrefixDir = DirectoryInfo (Path.Combine (prefix, "lib", UNIX_NAME))
let binPrefixDir = DirectoryInfo (Path.Combine (prefix, "bin"))

let wrapperScript = """#!/usr/bin/env bash
set -eo pipefail

if [[ $SNAP ]]; then
    PKG_DIR=$SNAP/usr
    export MONO_PATH=$PKG_DIR/lib/mono/4.5:$PKG_DIR/lib/cli/gtk-sharp-2.0:$PKG_DIR/lib/cli/glib-sharp-2.0:$PKG_DIR/lib/cli/atk-sharp-2.0:$PKG_DIR/lib/cli/gdk-sharp-2.0:$PKG_DIR/lib/cli/pango-sharp-2.0:$MONO_PATH
    export MONO_CONFIG=$SNAP/etc/mono/config
    export MONO_CFG_DIR=$SNAP/etc
    export MONO_REGISTRY_PATH=~/.mono/registry
    export MONO_GAC_PREFIX=$PKG_DIR/lib/mono/gac/
fi

DIR_OF_THIS_SCRIPT=$(dirname "$(realpath "$0")")
FRONTEND_PATH="$DIR_OF_THIS_SCRIPT/../lib/$UNIX_NAME/$GWALLET_PROJECT.exe"
exec mono "$FRONTEND_PATH" "$@"
"""

let nugetExe = Path.Combine(rootDir.FullName, ".nuget", "nuget.exe") |> FileInfo
let nugetPackagesSubDirName = "packages"

let PrintNugetVersion () =
    if not (nugetExe.Exists) then
        false
    else
        let nugetCmd =
            match Misc.GuessPlatform() with
            | Misc.Platform.Windows ->
                { Command = nugetExe.FullName; Arguments = String.Empty }
            | _ -> { Command = "mono"; Arguments = nugetExe.FullName }
        let nugetProc = Process.Execute (nugetCmd, Echo.Off)
        Console.WriteLine nugetProc.Output.StdOut
        if nugetProc.ExitCode = 0 then
            true
        else
            Console.Error.WriteLine nugetProc.Output.StdErr
            Console.WriteLine()
            failwith "nuget process' output contained errors ^"

let BuildSolution buildTool solutionFileName binaryConfig maybeConstant extraOptions =
    let configOption = sprintf "/p:Configuration=%s" (binaryConfig.ToString())
    let defineConstantsFromBuildConfig =
        match buildConfigContents |> Map.tryFind "DefineConstants" with
        | Some constants -> constants.Split([|";"|], StringSplitOptions.RemoveEmptyEntries) |> Seq.ofArray
        | None -> Seq.empty
    let allDefineConstants =
        match maybeConstant with
        | Some constant -> Seq.append [constant] defineConstantsFromBuildConfig
        | None -> defineConstantsFromBuildConfig

    let configOptions =
        if allDefineConstants.Any() then
            // FIXME: we shouldn't override the project's DefineConstants, but rather set "ExtraDefineConstants"
            // from the command line, and merge them later in the project file: see https://stackoverflow.com/a/32326853/544947
            sprintf "%s;DefineConstants=%s" configOption (String.Join(";", allDefineConstants))
        else
            configOption
    let buildArgs = sprintf "%s %s %s"
                            solutionFileName
                            configOptions
                            extraOptions
    let buildProcess = Process.Execute ({ Command = buildTool; Arguments = buildArgs }, Echo.All)
    if (buildProcess.ExitCode <> 0) then
        Console.Error.WriteLine (sprintf "%s build failed" buildTool)
        PrintNugetVersion() |> ignore
        Environment.Exit 1

let JustBuild binaryConfig maybeConstant: Frontend*FileInfo =
    printfn "Building in %s mode..." (binaryConfig.ToString().ToUpper())
    let buildTool = Map.tryFind "BuildTool" buildConfigContents
    if buildTool.IsNone then
        failwith "A BuildTool should have been chosen by the configure script, please report this bug"

    BuildSolution buildTool.Value DEFAULT_SOLUTION_FILE binaryConfig maybeConstant String.Empty

    let frontend =

        // older mono versions (which only have xbuild, not msbuild) can't compile .NET Standard assemblies
        if buildTool.Value = "msbuild" then
            let frontendSolutionFile,isGtkPresent =
                match Misc.GuessPlatform () with
                | Misc.Platform.Mac -> Some MAC_SOLUTION_FILE, false
                | Misc.Platform.Linux ->
                    let pkgConfigForGtkProc = Process.Execute({ Command = "pkg-config"; Arguments = "gtk-sharp-2.0" }, Echo.All)
                    let isGtkPresent =
                        (0 = pkgConfigForGtkProc.ExitCode)
                    Some LINUX_SOLUTION_FILE, isGtkPresent
                | _ -> None, false

            // somehow, msbuild doesn't restore the frontend dependencies (e.g. Xamarin.Forms) when targetting
            // the {LINUX|MAC}_SOLUTION_FILE below, so we need this workaround. TODO: report this bug
            let ExplicitRestore projectOrSolutionRelativePath =
                let nugetWorkaroundArgs =
                    sprintf
                        "%s restore %s -SolutionDirectory ."
                        nugetExe.FullName projectOrSolutionRelativePath
                Process.SafeExecute({ Command = "mono"; Arguments = nugetWorkaroundArgs }, Echo.All)
                |> ignore

            let MSBuildRestoreAndBuild solutionFile =
                BuildSolution "msbuild" solutionFile binaryConfig maybeConstant "/t:Restore"
                // TODO: report as a bug the fact that /t:Restore;Build doesn't work while /t:Restore and later /t:Build does
                BuildSolution "msbuild" solutionFile binaryConfig maybeConstant "/t:Build"

            if isGtkPresent then

                ExplicitRestore <| sprintf "src/%s/%s.fsproj" GTK_FRONTEND GTK_FRONTEND

            match frontendSolutionFile with
            | Some solution ->

                ExplicitRestore solution

                MSBuildRestoreAndBuild solution

            | _ -> ()

            if isGtkPresent then
                Frontend.Gtk
            else
                Frontend.Console
        else
            Frontend.Console

    let scriptName = sprintf "%s-%s" UNIX_NAME (frontend.ToString().ToLower())
    let launcherScriptFile = Path.Combine (scriptsDir.FullName, "bin", scriptName) |> FileInfo
    Directory.CreateDirectory(launcherScriptFile.Directory.FullName) |> ignore
    let wrapperScriptWithPaths =
        wrapperScript.Replace("$UNIX_NAME", UNIX_NAME)
                     .Replace("$GWALLET_PROJECT", frontend.GetExecutableName())
    File.WriteAllText (launcherScriptFile.FullName, wrapperScriptWithPaths)
    frontend,launcherScriptFile

let MakeCheckCommand (commandName: string) =
    if not (Process.CommandWorksInShell commandName) then
        Console.Error.WriteLine (sprintf "%s not found, please install it first" commandName)
        Environment.Exit 1

let GetPathToFrontend (frontend: Frontend) (binaryConfig: BinaryConfig): DirectoryInfo*FileInfo =
    let frontendProjName = frontend.GetProjectName()
    let dir = Path.Combine (rootDir.FullName, "src", frontendProjName, "bin", binaryConfig.ToString())
                  |> DirectoryInfo
    let mainExecFile = dir.GetFiles("*.exe", SearchOption.TopDirectoryOnly).Single()
    dir,mainExecFile

let GetPathToBackend () =
    Path.Combine (rootDir.FullName, "src", BACKEND)

let MakeAll maybeConstant =
    let buildConfig = BinaryConfig.Debug
    let frontend,_ = JustBuild buildConfig maybeConstant
    frontend,buildConfig

let RunFrontend (frontend: Frontend) (buildConfig: BinaryConfig) (maybeArgs: Option<string>) =
    let monoVersion = Map.tryFind "MonoPkgConfigVersion" buildConfigContents

    let frontendDir,frontendExecutable = GetPathToFrontend frontend buildConfig
    let pathToFrontend = frontendExecutable.FullName

    let fileName, finalArgs =
        match maybeArgs with
        | None | Some "" -> pathToFrontend,String.Empty
        | Some args -> pathToFrontend,args

    let startInfo = ProcessStartInfo(FileName = fileName, Arguments = finalArgs, UseShellExecute = false)
    startInfo.EnvironmentVariables.["MONO_ENV_OPTIONS"] <- "--debug"

    let proc = Process.Start startInfo
    proc.WaitForExit()
    proc

let maybeTarget = GatherTarget (Misc.FsxArguments(), None)
match maybeTarget with
| None ->
    MakeAll None |> ignore

| Some("release") ->
    JustBuild BinaryConfig.Release None
        |> ignore

| Some "nuget" ->
    Console.WriteLine "This target is for debugging purposes."

    if not (PrintNugetVersion()) then
        Console.Error.WriteLine "Nuget executable has not been downloaded yet, try `make` alone first"
        Environment.Exit 1

| Some("zip") ->
    let zipCommand = "zip"
    MakeCheckCommand zipCommand

    let version = Misc.GetCurrentVersion(rootDir).ToString()

    let release = BinaryConfig.Release
    let frontend,script = JustBuild release None
    let binDir = "bin"
    Directory.CreateDirectory(binDir) |> ignore

    let zipNameWithoutExtension = sprintf "%s-v%s" script.Name version
    let zipName = sprintf "%s.zip" zipNameWithoutExtension
    let pathToZip = Path.Combine(binDir, zipName)
    if (File.Exists (pathToZip)) then
        File.Delete (pathToZip)

    let pathToFolderToBeZipped = Path.Combine(binDir, zipNameWithoutExtension)
    if (Directory.Exists (pathToFolderToBeZipped)) then
        Directory.Delete (pathToFolderToBeZipped, true)

    let pathToFrontend,_ = GetPathToFrontend frontend release
    let zipRun = Process.Execute({ Command = "cp"
                                   Arguments = sprintf "-rfvp %s %s" pathToFrontend.FullName pathToFolderToBeZipped },
                                 Echo.All)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "Precopy for ZIP compression failed"
        Environment.Exit 1

    let previousCurrentDir = Directory.GetCurrentDirectory()
    Directory.SetCurrentDirectory binDir
    let zipLaunch = { Command = zipCommand
                      Arguments = sprintf "%s -r %s %s"
                                      zipCommand zipName zipNameWithoutExtension }
    let zipRun = Process.Execute(zipLaunch, Echo.All)
    if (zipRun.ExitCode <> 0) then
        Console.Error.WriteLine "ZIP compression failed"
        Environment.Exit 1
    Directory.SetCurrentDirectory previousCurrentDir

| Some("check") ->
    Console.WriteLine "Running tests..."
    Console.WriteLine ()

    // so that we get file names in stack traces
    Environment.SetEnvironmentVariable("MONO_ENV_OPTIONS", "--debug")

    let testAssemblyName = "GWallet.Backend.Tests"
    let testAssembly = Path.Combine(rootDir.FullName, "src", testAssemblyName, "bin",
                                    testAssemblyName + ".dll") |> FileInfo
    if not testAssembly.Exists then
        failwithf "File not found: %s" testAssembly.FullName

    let runnerCommand =
        match Misc.GuessPlatform() with
        | Misc.Platform.Linux ->
            let nunitCommand = "nunit-console"
            MakeCheckCommand nunitCommand

            { Command = nunitCommand; Arguments = testAssembly.FullName }
        | _ ->
            let nunitVersion = "2.7.1"
            if not nugetExe.Exists then
                MakeAll None |> ignore

            let nugetInstallCommand =
                {
                    Command = nugetExe.FullName
                    Arguments = sprintf "install NUnit.Runners -Version %s -OutputDirectory %s"
                                        nunitVersion nugetPackagesSubDirName
                }
            Process.SafeExecute(nugetInstallCommand, Echo.All)
                |> ignore

            {
                Command = Path.Combine(nugetPackagesSubDirName,
                                       sprintf "NUnit.Runners.%s" nunitVersion,
                                       "tools",
                                       "nunit-console.exe")
                Arguments = testAssembly.FullName
            }

    let nunitRun = Process.Execute(runnerCommand,
                                   Echo.All)
    if (nunitRun.ExitCode <> 0) then
        Console.Error.WriteLine "Tests failed"
        Environment.Exit 1

| Some("install") ->
    let buildConfig = BinaryConfig.Release
    let frontend,launcherScriptFile = JustBuild buildConfig None

    let mainBinariesDir binaryConfig = DirectoryInfo (Path.Combine(rootDir.FullName,
                                                                   "src",
                                                                   frontend.GetProjectName(),
                                                                   "bin",
                                                                   binaryConfig.ToString()))


    let destDirUpperCase = Environment.GetEnvironmentVariable "DESTDIR"
    let destDirLowerCase = Environment.GetEnvironmentVariable "DestDir"
    let destDir =
        if not (String.IsNullOrEmpty destDirUpperCase) then
            destDirUpperCase |> DirectoryInfo
        elif not (String.IsNullOrEmpty destDirLowerCase) then
            destDirLowerCase |> DirectoryInfo
        else
            prefix |> DirectoryInfo

    let libDestDir = Path.Combine(destDir.FullName, "lib", UNIX_NAME) |> DirectoryInfo
    let binDestDir = Path.Combine(destDir.FullName, "bin") |> DirectoryInfo

    Console.WriteLine "Installing..."
    Console.WriteLine ()
    Misc.CopyDirectoryRecursively (mainBinariesDir buildConfig, libDestDir, [])

    let finalLauncherScriptInDestDir = Path.Combine(binDestDir.FullName, launcherScriptFile.Name) |> FileInfo
    if not (Directory.Exists(finalLauncherScriptInDestDir.Directory.FullName)) then
        Directory.CreateDirectory(finalLauncherScriptInDestDir.Directory.FullName) |> ignore
    File.Copy(launcherScriptFile.FullName, finalLauncherScriptInDestDir.FullName, true)
    if Process.Execute({ Command = "chmod"; Arguments = sprintf "ugo+x %s" finalLauncherScriptInDestDir.FullName },
                        Echo.Off).ExitCode <> 0 then
        failwith "Unexpected chmod failure, please report this bug"

| Some("run") ->
    let frontend,buildConfig = MakeAll None
    RunFrontend frontend buildConfig None
        |> ignore

| Some "update-servers" ->
    let _,buildConfig = MakeAll None
    Directory.SetCurrentDirectory (GetPathToBackend())
    let proc1 = RunFrontend Frontend.Console buildConfig (Some "--update-servers-file")
    if proc1.ExitCode <> 0 then
        Environment.Exit proc1.ExitCode
    else
        let proc2 = RunFrontend Frontend.Console buildConfig (Some "--update-servers-stats")
        Environment.Exit proc2.ExitCode

| Some "strict" ->
    MakeAll <| Some "STRICTER_COMPILATION_BUT_WITH_REFLECTION_AT_RUNTIME"
        |> ignore

| Some "sanitycheck" ->
    let FindOffendingPrintfUsage () =
        let findScript = Path.Combine(rootDir.FullName, "scripts", "find.fsx")
        let fsxRunner =
            match Misc.GuessPlatform() with
            | Misc.Platform.Windows ->
                Path.Combine(rootDir.FullName, "scripts", "fsi.bat")
            | _ ->
                let fsxRunnerEnvVar = Environment.GetEnvironmentVariable "FsxRunner"
                if String.IsNullOrEmpty fsxRunnerEnvVar then
                    failwith "FsxRunner env var should have been passed to make.sh"
                fsxRunnerEnvVar
        let excludeFolders =
            String.Format("scripts{0}" +
                          "src{1}GWallet.Frontend.Console{0}" +
                          "src{1}GWallet.Backend.Tests{0}" +
                          "src{1}GWallet.Backend{1}FSharpUtil.fs",
                          Path.PathSeparator, Path.DirectorySeparatorChar)

        let proc =
            {
                Command = fsxRunner
                Arguments = sprintf "%s --exclude=%s %s"
                                    findScript
                                    excludeFolders
                                    "printf failwithf"
            }
        let findProc = Process.SafeExecute (proc, Echo.All)
        if findProc.Output.StdOut.Trim().Length > 0 then
            Console.Error.WriteLine "Illegal usage of printf/printfn/sprintf/sprintfn/failwithf detected; use SPrintF1/SPrintF2/... instead"
            Environment.Exit 1

    let SanityCheckNugetPackages () =

        let DEFAULT_NUGET_PACKAGES_SUBFOLDER_NAME = "packages"

        let notSubmodule (dir: DirectoryInfo): bool =
            let getSubmoduleDirsForThisRepo (): seq<DirectoryInfo> =
                let regex = Regex("path\s*=\s*([^\s]+)")
                seq {
                    for regexMatch in regex.Matches (File.ReadAllText (".gitmodules")) do
                        let submoduleFolderRelativePath = regexMatch.Groups.[1].ToString ()
                        let submoduleFolder =
                            DirectoryInfo (
                                Path.Combine (Directory.GetCurrentDirectory (), submoduleFolderRelativePath)
                            )
                        yield submoduleFolder
                }
            not (getSubmoduleDirsForThisRepo().Any (fun d -> dir.FullName = d.FullName))

        let blackList (dir: DirectoryInfo): bool =
            dir.Name.StartsWith "GWallet.Frontend.XF" && not (dir.Name.EndsWith "Gtk")

        let notDiscard (dir: DirectoryInfo): bool =
            not (blackList dir) && notSubmodule dir

        let sanityCheckNugetPackagesFromSolution (sol: FileInfo) =
            let rec findPackagesDotConfigFiles (dir: DirectoryInfo): seq<FileInfo> =
                dir.Refresh ()
                seq {
                    for file in dir.EnumerateFiles () do
                        if file.Name.ToLower () = "packages.config" then
                            yield file
                    for subdir in dir.EnumerateDirectories().Where notDiscard do
                        for file in findPackagesDotConfigFiles subdir do
                            yield file
                }

            let rec findNuspecFiles (dir: DirectoryInfo): seq<FileInfo> =
                dir.Refresh ()
                seq {
                    for file in dir.EnumerateFiles () do
                        if (file.Name.ToLower ()).EndsWith ".nuspec" then
                            yield file
                    for subdir in dir.EnumerateDirectories().Where notDiscard do
                        for file in findNuspecFiles subdir do
                            yield file
                }

            let getPackageTree (solDir: DirectoryInfo): Map<ComparableFileInfo,seq<PackageInfo>> =
                let packagesConfigFiles = findPackagesDotConfigFiles solDir
                let nuspecFiles = findNuspecFiles solDir
                seq {
                    for packagesConfigFile in packagesConfigFiles do
                        let xmlDoc = XDocument.Load packagesConfigFile.FullName
                        for descendant in xmlDoc.Descendants () do
                            if descendant.Name.LocalName.ToLower() = "package" then
                                let id = descendant.Attributes().Single(fun attr -> attr.Name.LocalName = "id").Value
                                let version = descendant.Attributes().Single(fun attr -> attr.Name.LocalName = "version").Value
                                let reqReinstall = descendant.Attributes().Any(fun attr -> attr.Name.LocalName = "requireReinstallation")
                                yield { File = packagesConfigFile }, { PackageId = id; PackageVersion = version; ReqReinstall = Some reqReinstall }

                    for nuspecFile in nuspecFiles do
                        let xmlDoc = XDocument.Load nuspecFile.FullName

                        let nsOpt =
                            let nsString = xmlDoc.Root.Name.Namespace.ToString()
                            if String.IsNullOrEmpty nsString then
                                None
                            else
                                let nsManager = XmlNamespaceManager(NameTable())
                                let nsPrefix = "x"
                                nsManager.AddNamespace(nsPrefix, nsString)
                                if nsString <> "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd" then
                                    Console.Error.WriteLine "Warning: the namespace URL doesn't match expectations, nuspec's XPath query may result in no elements"
                                Some(nsManager, sprintf "%s:" nsPrefix)
                        let query = "//{0}dependency"
                        let dependencies =
                            match nsOpt with
                            | None ->
                                let fixedQuery = String.Format(query, String.Empty)
                                xmlDoc.XPathSelectElements fixedQuery
                            | Some (nsManager, nsPrefix) ->
                                let fixedQuery = String.Format(query, nsPrefix)
                                xmlDoc.XPathSelectElements(fixedQuery, nsManager)

                        for dependency in dependencies do
                            let id = dependency.Attributes().Single(fun attr -> attr.Name.LocalName = "id").Value
                            let version = dependency.Attributes().Single(fun attr -> attr.Name.LocalName = "version").Value
                            yield { File = nuspecFile }, { PackageId = id; PackageVersion = version; ReqReinstall = None }
                } |> MapHelper.MergeIntoMap

            let getAllPackageIdsAndVersions (packageTree: Map<ComparableFileInfo,seq<PackageInfo>>): Map<PackageInfo,seq<DependencyHolder>> =
                seq {
                    for KeyValue (dependencyHolderFile, pkgs) in packageTree do
                        for pkg in pkgs do
                            yield pkg, dependencyHolderFile.DependencyHolderName
                } |> MapHelper.MergeIntoMap

            let getDirectoryNamesForPackagesSet (packages: Map<PackageInfo,seq<DependencyHolder>>): Map<string,seq<DependencyHolder>> =
                seq {
                    for KeyValue (package, prjs) in packages do
                        yield (sprintf "%s.%s" package.PackageId package.PackageVersion), prjs
                } |> Map.ofSeq

            let findMissingPackageDirs (solDir: DirectoryInfo) (idealPackageDirs: Map<string,seq<DependencyHolder>>): Map<string,seq<DependencyHolder>> =
                solDir.Refresh ()
                let packagesSubFolder = Path.Combine (solDir.FullName, DEFAULT_NUGET_PACKAGES_SUBFOLDER_NAME) |> DirectoryInfo
                if not packagesSubFolder.Exists then
                    failwithf "'%s' subdir under solution dir %s doesn't exist, run `make` first"
                        DEFAULT_NUGET_PACKAGES_SUBFOLDER_NAME
                        packagesSubFolder.FullName
                let packageDirsAbsolutePaths = packagesSubFolder.EnumerateDirectories().Select (fun dir -> dir.FullName)
                if not (packageDirsAbsolutePaths.Any()) then
                    Console.Error.WriteLine (
                        sprintf "'%s' subdir under solution dir %s doesn't contain any packages"
                            DEFAULT_NUGET_PACKAGES_SUBFOLDER_NAME
                            packagesSubFolder.FullName
                    )
                    Console.Error.WriteLine "Forgot to `git submodule update --init`?"
                    Environment.Exit 1

                seq {
                    for KeyValue (packageDirNameThatShouldExist, prjs) in idealPackageDirs do
                        if not (packageDirsAbsolutePaths.Contains (Path.Combine(packagesSubFolder.FullName, packageDirNameThatShouldExist))) then
                            yield packageDirNameThatShouldExist, prjs
                } |> Map.ofSeq

            let findExcessPackageDirs (solDir: DirectoryInfo) (idealPackageDirs: Map<string,seq<DependencyHolder>>): seq<string> =
                solDir.Refresh ()
                let packagesSubFolder = Path.Combine (solDir.FullName, "packages") |> DirectoryInfo
                if not (packagesSubFolder.Exists) then
                    failwithf "'%s' subdir under solution dir %s doesn't exist, run `make` first"
                        DEFAULT_NUGET_PACKAGES_SUBFOLDER_NAME
                        packagesSubFolder.FullName
                // "src" is a directory for source codes and build scripts,
                // not for packages, so we need to exclude it from here
                let packageDirNames = packagesSubFolder.EnumerateDirectories().Select(fun dir -> dir.Name).Except(["src"])
                if not (packageDirNames.Any()) then
                    failwithf "'%s' subdir under solution dir %s doesn't contain any packages"
                        DEFAULT_NUGET_PACKAGES_SUBFOLDER_NAME
                        packagesSubFolder.FullName
                let packageDirsThatShouldExist = MapHelper.GetKeysOfMap idealPackageDirs
                seq {
                    for packageDirThatExists in packageDirNames do
                        if not (packageDirsThatShouldExist.Contains packageDirThatExists) then
                            yield packageDirThatExists
                }

            let findPackagesWithMoreThanOneVersion
                (packageTree: Map<ComparableFileInfo,seq<PackageInfo>>)
                : Map<string,seq<ComparableFileInfo*PackageInfo>> =

                let getAllPackageInfos (packages: Map<ComparableFileInfo,seq<PackageInfo>>) =
                    let pkgInfos =
                        seq {
                            for KeyValue (_, pkgs) in packages do
                                for pkg in pkgs do
                                    yield pkg
                        }
                    Set pkgInfos

                let getAllPackageVersionsForPackageId (packages: seq<PackageInfo>) (packageId: string) =
                    seq {
                        for package in packages do
                            if package.PackageId = packageId then
                                yield package.PackageVersion
                    } |> Set

                let packageInfos = getAllPackageInfos packageTree
                let packageIdsWithMoreThan1Version =
                    seq {
                        for packageId in packageInfos.Select (fun pkg -> pkg.PackageId) do
                            let versions = getAllPackageVersionsForPackageId packageInfos packageId
                            if versions.Count > 1 then
                                yield packageId
                    }
                if not (packageIdsWithMoreThan1Version.Any()) then
                    Map.empty
                else
                    seq {
                        for pkgId in packageIdsWithMoreThan1Version do
                            let pkgs = seq {
                                for KeyValue (file, packageInfos) in packageTree do
                                    for pkg in packageInfos do
                                        if pkg.PackageId = pkgId then
                                            yield file, pkg
                            }
                            yield pkgId, pkgs
                    } |> Map.ofSeq


            let solDir = sol.Directory
            solDir.Refresh ()
            let packageTree = getPackageTree solDir
            let packages = getAllPackageIdsAndVersions packageTree
            Console.WriteLine(sprintf "%d nuget packages found for solution in directory %s" packages.Count solDir.Name)
            let idealDirList = getDirectoryNamesForPackagesSet packages

            let missingPackageDirs = findMissingPackageDirs solDir idealDirList
            if missingPackageDirs.Any () then
                for KeyValue(missingPkg, depHolders) in missingPackageDirs do
                    let depHolderNames = String.Join(",", depHolders.Select(fun dh -> dh.Name))
                    Console.Error.WriteLine (sprintf "Missing folder for nuget package in submodule: %s (referenced from %s)" missingPkg depHolderNames)
                Environment.Exit 1

            let excessPackageDirs = findExcessPackageDirs solDir idealDirList
            if excessPackageDirs.Any () then
                let advice = "remove it with git filter-branch to avoid needless bandwidth: http://stackoverflow.com/a/17824718/6503091"
                for excessPkg in excessPackageDirs do
                    Console.Error.WriteLine(sprintf "Unused nuget package folder: %s (%s)" excessPkg advice)
                Environment.Exit 1

            let pkgWithMoreThan1VersionPrint (key: string) (packageInfos: seq<ComparableFileInfo*PackageInfo>) =
                Console.Error.WriteLine (sprintf "Package found with more than one version: %s. All occurrences:" key)
                for file,pkgInfo in packageInfos do
                    Console.Error.WriteLine (sprintf "* Version: %s. Dependency holder: %s" pkgInfo.PackageVersion file.DependencyHolderName.Name)
            let packagesWithMoreThanOneVersion = findPackagesWithMoreThanOneVersion packageTree
            if packagesWithMoreThanOneVersion.Any() then
                Map.iter pkgWithMoreThan1VersionPrint packagesWithMoreThanOneVersion
                Environment.Exit 1

            let findPackagesWithSomeReqReinstallAttrib
                (packageTree: Map<ComparableFileInfo,seq<PackageInfo>>)
                : seq<ComparableFileInfo*PackageInfo> =
                seq {
                    for KeyValue (file, packageInfos) in packageTree do
                        for pkg in packageInfos do
                            match pkg.ReqReinstall with
                            | Some true ->
                                yield file, pkg
                            | _ -> ()
                }
            let packagesWithWithSomeReqReinstallAttrib = findPackagesWithSomeReqReinstallAttrib packageTree
            if packagesWithWithSomeReqReinstallAttrib.Any() then
                Console.Error.WriteLine (
                    sprintf "Packages found with some RequireReinstall attribute (please reinstall it before pushing):"
                )
                for file,pkg in packagesWithWithSomeReqReinstallAttrib do
                    Console.Error.WriteLine (
                        sprintf "* Name: %s. Project: %s" pkg.PackageId file.DependencyHolderName.Name
                    )
                Environment.Exit 1

            Console.WriteLine (sprintf "Nuget sanity check succeeded for solution dir %s" solDir.FullName)


        let rec findSolutions (dir: DirectoryInfo): seq<FileInfo> =
            dir.Refresh ()
            seq {
                // FIXME: avoid returning duplicates? (in case there are 2 .sln files in the same dir...)
                for file in dir.EnumerateFiles () do
                    if file.Name.ToLower().EndsWith ".sln" then
                        yield file
                for subdir in dir.EnumerateDirectories().Where notDiscard do
                    for solutionDir in findSolutions subdir do
                        yield solutionDir
            }

        let solutions = Directory.GetCurrentDirectory() |> DirectoryInfo |> findSolutions
        for sol in solutions do
            sanityCheckNugetPackagesFromSolution sol

    FindOffendingPrintfUsage()
    SanityCheckNugetPackages()

| Some(someOtherTarget) ->
    Console.Error.WriteLine("Unrecognized target: " + someOtherTarget)
    Environment.Exit 2
