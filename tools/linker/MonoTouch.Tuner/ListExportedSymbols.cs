﻿using System;
using System.Collections.Generic;

using Mono.Cecil;
using Mono.Linker;
using Mono.Linker.Steps;
using Mono.Tuner;

using Xamarin.Bundler;
using Xamarin.Linker;
using Xamarin.Tuner;

namespace MonoTouch.Tuner
{
	public class ListExportedSymbols : BaseStep
	{
		PInvokeWrapperGenerator state;
		bool skip_sdk_assemblies;

		public DerivedLinkContext DerivedLinkContext {
			get {
				return (DerivedLinkContext) Context;
			}
		}

		internal ListExportedSymbols (PInvokeWrapperGenerator state, bool skip_sdk_assemblies = false)
		{
			this.state = state;
			this.skip_sdk_assemblies = skip_sdk_assemblies;
		}

		protected override void ProcessAssembly (AssemblyDefinition assembly)
		{
			base.ProcessAssembly (assembly);

			if (Context.Annotations.GetAction (assembly) == AssemblyAction.Delete)
				return;

			if (skip_sdk_assemblies && Profile.IsSdkAssembly (assembly))
				return;

			if (!assembly.MainModule.HasTypes)
				return;

			var hasSymbols = false;
			if (assembly.MainModule.HasModuleReferences) {
				hasSymbols = true;
			} else if (assembly.MainModule.HasTypeReference (Namespaces.Foundation + ".FieldAttribute")) {
				hasSymbols = true;
			}
			if (!hasSymbols)
				return;

			foreach (var type in assembly.MainModule.Types)
				ProcessType (type);
		}

		void ProcessType (TypeDefinition type)
		{
			if (type.HasNestedTypes) {
				foreach (var nested in type.NestedTypes)
					ProcessType (nested);
			}

			if (type.HasMethods) {
				foreach (var method in type.Methods)
					ProcessMethod (method);
			}

			var registerAttribute = DerivedLinkContext.StaticRegistrar?.GetRegisterAttribute (type);
			if (registerAttribute != null && registerAttribute.IsWrapper && !DerivedLinkContext.StaticRegistrar.HasProtocolAttribute (type)) {
				var exportedName = DerivedLinkContext.StaticRegistrar.GetExportedTypeName (type, registerAttribute);
				DerivedLinkContext.ObjectiveCClasses [exportedName] = type;
			}
		}

		void ProcessMethod (MethodDefinition method)
		{
			if (method.IsPInvokeImpl && method.HasPInvokeInfo && method.PInvokeInfo != null) {
				var pinfo = method.PInvokeInfo;
				if (pinfo.Module.Name == "__Internal")
					DerivedLinkContext.GetRequiredSymbolList (pinfo.EntryPoint).Add (method);

				if (state != null) {
					switch (pinfo.EntryPoint) {
					case "objc_msgSend":
					case "objc_msgSendSuper":
					case "objc_msgSend_stret":
					case "objc_msgSendSuper_stret":
					case "objc_msgSend_fpret":
						state.ProcessMethod (method);
						break;
					default:
						return;
					}
				}
			}

			if (MarkStep.IsPropertyMethod (method)) {
				var property = MarkStep.GetProperty (method);
				object symbol;
				// The Field attribute may have been linked away, but we've stored it in an annotation.
				if (property != null && Context.Annotations.GetCustomAnnotations ("ExportedFields").TryGetValue (property, out symbol)) {
					DerivedLinkContext.GetRequiredSymbolList ((string) symbol).Add (property);
				}
			}
		}
	}
}
