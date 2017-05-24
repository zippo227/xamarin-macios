//
// MmpTask.cs
//
// Author:
//   Aaron Bockover <abock@xamarin.com>
//
// Copyright 2014 Xamarin Inc.

using System;
using System.IO;
using System.Collections.Generic;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Xamarin.MacDev.Tasks;
using Xamarin.MacDev;

namespace Xamarin.Mac.Tasks
{
	public class MmpTaskBase : ToolTask
	{
		protected override string ToolName {
			get { return "mmp"; }
		}

		public string SessionId { get; set; }

		[Required]
		public string AppBundleDir { get; set; }

		[Required]
		public string FrameworkRoot { get; set; }

		[Required]
		public string OutputPath { get; set; }

		[Required]
		public ITaskItem ApplicationAssembly { get; set; }

		[Required]
		public string HttpClientHandler { get; set; }

		[Required]
		public string TargetFrameworkIdentifier { get; set; }

		[Required]
		public string TargetFrameworkVersion { get; set; }

		[Required]
		public string SdkRoot {	get; set; }

		[Required]
		public ITaskItem AppManifest { get; set; }

		[Required]
		public string SdkVersion { get; set; }

		public bool IsAppExtension { get; set; }

		[Required]
		public bool EnableSGenConc { get; set; }

		public bool UseXamMacFullFramework { get; set; }

		public string ApplicationName { get; set; }
		public string ArchiveSymbols { get; set; }
		public string Architecture { get; set; }
		public string LinkMode { get; set; }
		public bool Debug { get; set; }
		public bool Profiling { get; set; }
		public string I18n { get; set; }
		public string ExtraArguments { get; set; }

		public string AotScope { get; set; }
		public bool HybridAotOption { get; set; }
		public string ExplicitAotAssemblies { get; set; }

		public ITaskItem [] ExplicitReferences { get; set; }
		public ITaskItem [] NativeReferences { get; set; }

		public string IntermediateOutputPath { get; set; }

		[Output]
		public ITaskItem[] NativeLibraries { get; set; }
		
		protected override string GenerateFullPathToTool ()
		{
			return Path.Combine (FrameworkRoot, "bin", "mmp");
		}

		protected override bool ValidateParameters ()
		{
			XamMacArch arch;

			return Enum.TryParse (Architecture, true, out arch);
		}

		protected override string GenerateCommandLineCommands ()
		{
			var args = new ProcessArgumentBuilder ();
			bool msym;

			args.Add ("/verbose");

			if (Debug)
				args.Add ("/debug");

			if (!string.IsNullOrEmpty (OutputPath))
				args.AddQuoted ("/output:" + Path.GetFullPath (OutputPath));

			if (!string.IsNullOrEmpty (ApplicationName))
				args.AddQuoted ("/name:" + ApplicationName);

			if (TargetFrameworkIdentifier == "Xamarin.Mac")
				args.Add ("/profile:Xamarin.Mac");
			else if (TargetFrameworkVersion.StartsWith ("v", StringComparison.Ordinal))
				args.Add ("/profile:" + TargetFrameworkVersion.Substring (1));

			XamMacArch arch;
			if (!Enum.TryParse (Architecture, true, out arch))
				arch = XamMacArch.Default;

			if (arch == XamMacArch.Default)
				arch = XamMacArch.x86_64;

			if (arch.HasFlag (XamMacArch.i386))
				args.Add ("/arch:i386");

			if (arch.HasFlag (XamMacArch.x86_64))
				args.Add ("/arch:x86_64");

			if (!string.IsNullOrEmpty (ArchiveSymbols) && bool.TryParse (ArchiveSymbols.Trim (), out msym))
				args.Add ("--msym:" + (msym ? "yes" : "no"));

			args.Add (string.Format ("--http-message-handler={0}", HttpClientHandler));

			if (AppManifest != null) {
				try {
					var plist = PDictionary.FromFile (AppManifest.ItemSpec);

					PString v;
					string minimumDeploymentTarget;

					if (!plist.TryGetValue (ManifestKeys.LSMinimumSystemVersion, out v) || string.IsNullOrEmpty (v.Value))
						minimumDeploymentTarget = SdkVersion;
					else
						minimumDeploymentTarget = v.Value;

					args.Add (string.Format("/minos={0}", minimumDeploymentTarget));
				}
				catch (Exception ex) {
					Log.LogWarning (null, null, null, AppManifest.ItemSpec, 0, 0, 0, 0, "Error loading '{0}': {1}", AppManifest.ItemSpec, ex.Message);
				}
			}

			if (Profiling)
				args.Add ("/profiling");

			if (EnableSGenConc)
				args.Add ("/sgen-conc");

			switch ((LinkMode ?? string.Empty).ToLower ()) {
			case "full":
				break;
			case "sdkonly":
				args.Add ("/linksdkonly");
				break;
			case "platform":
				args.Add ("/linkplatform");
				break;
			default:
				args.Add ("/nolink");
				break;
			}

			if (!string.IsNullOrEmpty (AotScope) && AotScope != "None") {
				var aot = $"--aot:{AotScope.ToLower ()}";
				if (HybridAotOption)
					aot += "|hybrid";

				if (!string.IsNullOrEmpty (ExplicitAotAssemblies))
					aot += $",{ExplicitAotAssemblies}";

				args.Add (aot);
			}

			if (!string.IsNullOrEmpty (I18n))
				args.AddQuoted ("/i18n:" + I18n);

			if (ExplicitReferences != null) {
				foreach (var asm in ExplicitReferences)
					args.AddQuoted ("/assembly:" + Path.GetFullPath (asm.ItemSpec));
			}

			if (!string.IsNullOrEmpty (ApplicationAssembly.ItemSpec)) {
				args.AddQuoted (Path.GetFullPath (ApplicationAssembly.ItemSpec));
			}

			if (!string.IsNullOrWhiteSpace (ExtraArguments))
				args.Add (ExtraArguments);

			if (NativeReferences != null) {
				foreach (var nr in NativeReferences)
					args.AddQuoted ("/native-reference:" + Path.GetFullPath (nr.ItemSpec));
			}
				
			if (IsAppExtension)
				args.AddQuoted ("/extension");

			args.Add ("/sdkroot");
			args.AddQuoted (SdkRoot);

			if (!string.IsNullOrEmpty (IntermediateOutputPath)) {
				Directory.CreateDirectory (IntermediateOutputPath);

				args.Add ("--cache");
				args.AddQuoted (Path.GetFullPath (IntermediateOutputPath));
			}

			return args.ToString ();
		}

		string GetMonoBundleDirName ()
		{
			if (!string.IsNullOrEmpty (ExtraArguments)) {
				var args = ProcessArgumentBuilder.Parse (ExtraArguments);

				for (int i = 0; i < args.Length; i++) {
					string arg;

					if (string.IsNullOrEmpty (args[i]))
						continue;

					if (args[i][0] == '/') {
						arg = args[i].Substring (1);
					} else if (args[i][0] == '-') {
						if (args[i].Length >= 2 && args[i][1] == '-')
							arg = args[i].Substring (2);
						else
							arg = args[i].Substring (1);
					} else {
						continue;
					}

					if (arg.StartsWith ("custom_bundle_name:", StringComparison.Ordinal) ||
					    arg.StartsWith ("custom_bundle_name=", StringComparison.Ordinal))
						return arg.Substring ("custom_bundle_name=".Length);

					if (arg == "custom_bundle_name" && i + 1 < args.Length)
						return args[i + 1];
				}
			}

			return "MonoBundle";
		}

		public override bool Execute ()
		{
			Log.LogTaskName ("Mmp");
			Log.LogTaskProperty ("AppBundleDir", AppBundleDir);
			Log.LogTaskProperty ("ApplicationAssembly", ApplicationAssembly);
			Log.LogTaskProperty ("ApplicationName", ApplicationName);
			Log.LogTaskProperty ("Architecture", Architecture);
			Log.LogTaskProperty ("ArchiveSymbols", ArchiveSymbols);
			Log.LogTaskProperty ("Debug", Debug);
			Log.LogTaskProperty ("EnableSGenConc", EnableSGenConc);
			Log.LogTaskProperty ("ExplicitReferences", ExplicitReferences);
			Log.LogTaskProperty ("ExtraArguments", ExtraArguments);
			Log.LogTaskProperty ("FrameworkRoot", FrameworkRoot);
			Log.LogTaskProperty ("I18n", I18n);
			Log.LogTaskProperty ("IntermediateOutputPath", IntermediateOutputPath);
			Log.LogTaskProperty ("LinkMode", LinkMode);
			Log.LogTaskProperty ("OutputPath", OutputPath);
			Log.LogTaskProperty ("SdkRoot", SdkRoot);
			Log.LogTaskProperty ("TargetFrameworkIdentifier", TargetFrameworkIdentifier);
			Log.LogTaskProperty ("TargetFrameworkVersion", TargetFrameworkVersion);
			Log.LogTaskProperty ("UseXamMacFullFramework", UseXamMacFullFramework);
			Log.LogTaskProperty ("Profiling", Profiling);
			Log.LogTaskProperty ("AppManifest", AppManifest);
			Log.LogTaskProperty ("SdkVersion", SdkVersion);
			Log.LogTaskProperty ("NativeReferences", NativeReferences);
			Log.LogTaskProperty ("IsAppExtension", IsAppExtension);
			Log.LogTaskProperty ("AotScope", AotScope);
			Log.LogTaskProperty ("HybridAotOption", HybridAotOption);
			Log.LogTaskProperty ("ExplicitAotAssemblies", ExplicitAotAssemblies);


			if (!base.Execute ())
				return false;

			var monoBundleDir = Path.Combine (AppBundleDir, "Contents", GetMonoBundleDirName ());

			try {
				var nativeLibrariesPath = Directory.EnumerateFiles (monoBundleDir, "*.dylib", SearchOption.AllDirectories);
				var nativeLibraryItems = new List<ITaskItem> ();

				foreach (var nativeLibrary in nativeLibrariesPath) {
					nativeLibraryItems.Add (new TaskItem (nativeLibrary));
				}

				NativeLibraries = nativeLibraryItems.ToArray ();
			} catch (Exception ex) {
				Log.LogError (null, null, null, AppBundleDir, 0, 0, 0, 0, "Could not get native libraries: {0}", ex.Message);
				return false;
			}

			return !Log.HasLoggedErrors;
		}

		protected override void LogEventsFromTextOutput (string singleLine, MessageImportance messageImportance)
		{
			try { // We first try to use the base logic, which shows up nicely in XS.
				base.LogEventsFromTextOutput (singleLine, messageImportance);
			}
			catch { // But when that fails, just output the message to the command line and XS will output it raw
				Log.LogMessage (messageImportance, "{0}", singleLine);
			}
		}
	}
}
