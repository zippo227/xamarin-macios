//
// StaticRegistrar.cs: The static registrar
// 
// Authors:
//   Rolf Bjarne Kvinge <rolf@xamarin.com>
//
// Copyright 2013 Xamarin Inc. 
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Xamarin.Bundler;

#if MTOUCH
using ProductException=Xamarin.Bundler.MonoTouchException;
#else
using ProductException=Xamarin.Bundler.MonoMacException;
#endif

using XamCore.Registrar;
using XamCore.Foundation;
using XamCore.ObjCRuntime;
using Mono.Cecil;
using Mono.Tuner;

namespace XamCore.Registrar {
	/*
	 * This class will automatically detect lines starting/ending with curly braces,
	 * and indent/unindent accordingly.
	 * 
	 * It doesn't cope with indentation due to other circumstances than
	 * curly braces (such as one-line if statements for instance). In this case
	 * call Indent/Unindent manually.
	 * 
	 * Also don't try to print '\n' directly, it'll get confused. Use
	 * the AppendLine/WriteLine family of methods instead.
	 */
	class AutoIndentStringBuilder : IDisposable {
		StringBuilder sb = new StringBuilder ();
		int indent;
		bool was_last_newline = true;

		public int Indentation {
			get { return indent; }
			set { indent = value; }
		}

		public AutoIndentStringBuilder ()
		{
		}

		public AutoIndentStringBuilder (int indentation)
		{
			indent = indentation;
		}

		public StringBuilder StringBuilder {
			get {
				return sb;
			}
		}

		void OutputIndentation ()
		{
			if (!was_last_newline)
				return;
			sb.Append ('\t', indent);
			was_last_newline = false;
		}

		void Output (string a)
		{
			if (a.StartsWith ("}", StringComparison.Ordinal))
				Unindent ();
			OutputIndentation ();
			sb.Append (a);
			if (a.EndsWith ("{", StringComparison.Ordinal))
				Indent ();
		}

		public AutoIndentStringBuilder AppendLine (AutoIndentStringBuilder isb)
		{
			if (isb.Length > 0) {
				sb.Append (isb.ToString ());
				AppendLine ();
			}

			return this;
		}

		public AutoIndentStringBuilder Append (AutoIndentStringBuilder isb)
		{
			if (isb.Length > 0)
				sb.Append (isb.ToString ());

			return this;
		}

		public AutoIndentStringBuilder Append (string value)
		{
			Output (value);
			return this;
		}

		public AutoIndentStringBuilder AppendFormat (string format, params object[] args)
		{
			Output (string.Format (format, args));
			return this;
		}

		public AutoIndentStringBuilder AppendLine (string value)
		{
			Output (value);
			sb.AppendLine ();
			was_last_newline = true;
			return this;
		}

		public AutoIndentStringBuilder AppendLine ()
		{
			sb.AppendLine ();
			was_last_newline = true;
			return this;
		}

		public AutoIndentStringBuilder AppendLine (string format, params object[] args)
		{
			Output (string.Format (format, args));
			sb.AppendLine ();
			was_last_newline = true;
			return this;
		}

		public AutoIndentStringBuilder Write (string value)
		{
			return Append (value);
		}

		public AutoIndentStringBuilder Write (string format, params object[] args)
		{
			return AppendFormat (format, args);
		}

		public AutoIndentStringBuilder WriteLine (string format, params object[] args)
		{
			return AppendLine (format, args);
		}

		public AutoIndentStringBuilder WriteLine (string value)
		{
			return AppendLine (value);
		}

		public AutoIndentStringBuilder WriteLine ()
		{
			return AppendLine ();
		}

		public AutoIndentStringBuilder WriteLine (AutoIndentStringBuilder isb)
		{
			return AppendLine (isb);
		}

		public void Replace (string find, string replace)
		{
			sb.Replace (find, replace);
		}

		public AutoIndentStringBuilder Indent ()
		{
			indent++;
			if (indent > 100)
				throw new ArgumentOutOfRangeException ("indent");
			return this;
		}

		public AutoIndentStringBuilder Unindent ()
		{
			indent--;
			return this;
		}

		public int Length {
			get { return sb.Length; }
		}

		public void Clear ()
		{
			sb.Clear ();
			was_last_newline = true;
		}

		public void Dispose ()
		{
			Clear ();
		}
		public override string ToString ()
		{
			return sb.ToString (0, sb.Length);
		}
	}

	static class SharedStatic {
		public static bool IsPlatformType (this TypeReference type, string @namespace, string name)
		{
			if (Registrar.IsDualBuild) {
				return type.Is (@namespace, name);
			} else {
				return type.Is (Registrar.CompatNamespace + "." + @namespace, name);
			}
		}

		public static bool TryGetAttributeImpl (ICustomAttributeProvider provider, string @namespace, string attributeName, out CustomAttribute attribute)
		{
			attribute = null;
			if (!provider.HasCustomAttributes)
				return false;

			foreach (CustomAttribute custom_attribute in provider.CustomAttributes) {
				if (!custom_attribute.Constructor.DeclaringType.Is (@namespace, attributeName))
					continue;

				attribute = custom_attribute;
				return true;
			}

			return false;
		}

		public static bool HasAttribute (ICustomAttributeProvider provider, string @namespace, string attributeName)
		{
			CustomAttribute attrib;
			return TryGetAttributeImpl (provider,  @namespace, attributeName, out attrib);
		}

		public static bool TypeMatch (IModifierType a, IModifierType b)
		{
			if (!TypeMatch (a.ModifierType, b.ModifierType))
				return false;

			return TypeMatch (a.ElementType, b.ElementType);
		}

		public static bool TypeMatch (TypeSpecification a, TypeSpecification b)
		{
			if (a is GenericInstanceType)
				return TypeMatch ((GenericInstanceType) a, (GenericInstanceType) b);

			if (a is IModifierType)
				return TypeMatch ((IModifierType) a, (IModifierType) b);

			return TypeMatch (a.ElementType, b.ElementType);
		}

		public static bool TypeMatch (GenericInstanceType a, GenericInstanceType b)
		{
			if (!TypeMatch (a.ElementType, b.ElementType))
				return false;

			if (a.GenericArguments.Count != b.GenericArguments.Count)
				return false;

			if (a.GenericArguments.Count == 0)
				return true;

			for (int i = 0; i < a.GenericArguments.Count; i++)
				if (!TypeMatch (a.GenericArguments [i], b.GenericArguments [i]))
					return false;

			return true;
		}

		public static bool TypeMatch (TypeReference a, TypeReference b)
		{
			if (a is GenericParameter)
				return true;

			if (a is TypeSpecification || b is TypeSpecification) {
				if (a.GetType () != b.GetType ())
					return false;

				return TypeMatch ((TypeSpecification) a, (TypeSpecification) b);
			}

			return a.FullName == b.FullName;
		}

		public static bool MethodMatch (MethodDefinition candidate, MethodDefinition method)
		{
			if (!candidate.IsVirtual)
				return false;

			if (candidate.Name != method.Name)
				return false;

			if (!TypeMatch (candidate.ReturnType, method.ReturnType))
				return false;

			if (!candidate.HasParameters)
				return !method.HasParameters;

			if (candidate.Parameters.Count != method.Parameters.Count)
				return false;

			for (int i = 0; i < candidate.Parameters.Count; i++)
				if (!TypeMatch (candidate.Parameters [i].ParameterType, method.Parameters [i].ParameterType))
					return false;

			return true;
		}

		static void CollectInterfaces (ref List<TypeDefinition> ifaces, TypeDefinition type)
		{
			if (type == null)
				return;
			
			if (type.BaseType != null)
				CollectInterfaces (ref ifaces, type.BaseType.Resolve ());

			if (!type.HasInterfaces)
				return;

			foreach (var iface in type.Interfaces) {
				var itd = iface.InterfaceType.Resolve ();
				CollectInterfaces (ref ifaces, itd);

				if (!HasAttribute (itd, Registrar.Foundation, Registrar.StringConstants.ProtocolAttribute))
					continue;

				if (ifaces == null) {
					ifaces = new List<TypeDefinition> ();
				} else if (ifaces.Contains (itd)) {
					continue;
				}

				ifaces.Add (itd);
			}
		}

		public static Dictionary<MethodDefinition, List<MethodDefinition>> PrepareInterfaceMethodMapping (TypeReference type)
		{
			TypeDefinition td = type.Resolve ();
			List<TypeDefinition> ifaces = null;
			List<MethodDefinition> iface_methods;
			Dictionary<MethodDefinition, List<MethodDefinition>> rv = null;

			CollectInterfaces (ref ifaces, td);

			if (ifaces == null)
				return null;

			iface_methods = new List<MethodDefinition> ();
			foreach (var iface in ifaces) {
				if (!iface.HasMethods)
					continue;

				foreach (var imethod in iface.Methods) {
					if (!iface_methods.Contains (imethod))
						iface_methods.Add (imethod);
				}
			}

			// We only care about implementators declared in 'type'.

			// First find all explicitly implemented methods.
			foreach (var impl in td.Methods) {
				if (!impl.HasOverrides)
					continue;

				foreach (var ifaceMethod in impl.Overrides) {
					var ifaceMethodDef = ifaceMethod.Resolve ();
					if (!iface_methods.Contains (ifaceMethodDef)) {
						// The type may implement interfaces which aren't protocol interfaces, so this is OK.
					} else {
						iface_methods.Remove (ifaceMethodDef);

						List<MethodDefinition> list;
						if (rv == null) {
							rv = new Dictionary<MethodDefinition, List<MethodDefinition>> ();
							rv [impl] = list = new List<MethodDefinition> ();
						} else if (!rv.TryGetValue (impl, out list)) {
							rv [impl] = list = new List<MethodDefinition> ();
						}
						list.Add (ifaceMethodDef);
					}
				}
			}

			// Next find all implicitly implemented methods.
			foreach (var ifaceMethod in iface_methods) {
				foreach (var impl in td.Methods) {
					if (impl.Name != ifaceMethod.Name)
						continue;

					if (!SharedStatic.MethodMatch (impl, ifaceMethod))
						continue;

					List<MethodDefinition> list;
					if (rv == null) {
						rv = new Dictionary<MethodDefinition, List<MethodDefinition>> ();
						rv [impl] = list = new List<MethodDefinition> ();
					} else if (!rv.TryGetValue (impl, out list)) {
						rv [impl] = list = new List<MethodDefinition> ();
					}
					list.Add (ifaceMethod);
				}
			}

			return rv;
		}

		public static TypeReference GetEnumUnderlyingType (TypeDefinition type)
		{
			foreach (FieldDefinition field in type.Fields)
				if (field.IsSpecialName && field.Name == "value__")
					return field.FieldType;

			return null;
		}

		public static string ToObjCType (TypeReference type)
		{
			var definition = type as TypeDefinition;
			if (definition != null)
				return ToObjCType (definition);

			if (type is TypeSpecification)
				return ToObjCType (((TypeSpecification) type).ElementType) + " *";

			definition = type.Resolve ();
			if (definition != null)
				return ToObjCType (definition);

			return "void *";
		}

		public static string ToObjCType (TypeDefinition type)
		{
			switch (type.FullName) {
			case "System.IntPtr": return "void *";
			case "System.SByte": return "unsigned char";
			case "System.Byte": return "signed char";
			case "System.Char": return "signed short";
			case "System.Int16": return "short";
			case "System.UInt16": return "unsigned short";
			case "System.Int32": return "int";
			case "System.UInt32": return "unsigned int";
			case "System.Int64": return "long long";
			case "System.UInt64": return "unsigned long long";
			case "System.Single": return "float";
			case "System.Double": return "double";
			case "System.Boolean": return "BOOL";
			case "System.Void": return "void";
			case "System.String": return "NSString";
			case Registrar.CompatNamespace + ".ObjCRuntime.Selector": return "SEL";
			case Registrar.CompatNamespace + ".ObjCRuntime.Class": return "Class";
			}

			if (IsNativeObject (type))
				return "id";

			if (IsDelegate (type))
				return "id";

			if (type.IsEnum)
				return ToObjCType (GetEnumUnderlyingType (type));

			if (type.IsValueType) {
				return "void *";
			}

			throw ErrorHelper.CreateError (4108, "The registrar cannot get the ObjectiveC type for managed type `{0}`.", type.FullName);
		}

		public static bool IsDelegate (TypeDefinition type)
		{
			while (type != null) {
				if (type.FullName == "System.Delegate")
					return true;

				type = type.BaseType != null ? type.BaseType.Resolve () : null;
			}

			return false;
		}

		public static bool IsNativeObject (TypeReference tr)
		{
			var gp = tr as GenericParameter;
			if (gp != null) {
				if (gp.HasConstraints) {
					foreach (var constraint in gp.Constraints) {
						if (IsNativeObject (constraint))
							return true;
					}
				}
				return false;
			}

			var type = tr.Resolve ();
			while (type != null) {
				if (type.HasInterfaces) {
					foreach (var iface in type.Interfaces)
						if (iface.InterfaceType.Is (Registrar.ObjCRuntime, Registrar.StringConstants.INativeObject))
							return true;
				}

				type = type.BaseType != null ? type.BaseType.Resolve () : null;
			}

			return tr.Is (Registrar.ObjCRuntime, Registrar.StringConstants.INativeObject);
		}
	}

	public interface IStaticRegistrar
	{
		void Generate (IEnumerable<AssemblyDefinition> assemblies, string header_path, string source_path);
		void GenerateSingleAssembly (IEnumerable<AssemblyDefinition> assemblies, string header_path, string source_path, string assembly);
		Mono.Linker.LinkContext LinkContext { get; set; }
	}

	class StaticRegistrar : Registrar, IStaticRegistrar {
		public Target Target { get; private set; }
		public bool IsSingleAssembly { get { return !string.IsNullOrEmpty (single_assembly); } }

		string single_assembly;
		IEnumerable<AssemblyDefinition> input_assemblies;
		Mono.Linker.LinkContext link_context;
		Dictionary<IMetadataTokenProvider, object> availability_annotations;

#if MONOMAC
		readonly Version MacOSTenTwelveVersion = new Version (10,12);
#endif

		public Mono.Linker.LinkContext LinkContext {
			get {
				return link_context;
			}
			set {
				link_context = value;
				availability_annotations = link_context?.Annotations.GetCustomAnnotations ("Availability");
			}
		}

		void Init (Application app)
		{
			this.App = app;
			trace = !LaxMode && (app.RegistrarOptions & RegistrarOptions.Trace) == RegistrarOptions.Trace;
		}

		public StaticRegistrar (Application app)
		{
			Init (app);
		}

		public StaticRegistrar (Target target)
		{
			Init (target.App);
			this.Target = target;
		}

		protected override bool LaxMode {
			get {
				return IsSingleAssembly;
			}
		}

		protected override void ReportError (int code, string message, params object[] args)
		{
			throw ErrorHelper.CreateError (code, message, args);
		}
		
		protected override void ReportWarning (int code, string message, params object[] args)
		{
			ErrorHelper.Show (ErrorHelper.CreateWarning (code, message, args));
		}

		protected override bool IsCorlibType (TypeReference type)
		{
			return type.Resolve ().Module.Assembly.Name.Name == "mscorlib";
		}

		public static int GetValueTypeSize (TypeDefinition type, bool is_64_bits)
		{
			switch (type.FullName) {
				case "System.Char": return 2;
				case "System.Boolean":
				case "System.SByte":
				case "System.Byte": return 1;
				case "System.Int16":
				case "System.UInt16": return 2;
				case "System.Single":
				case "System.Int32":
				case "System.UInt32": return 4;
				case "System.Double":
				case "System.Int64": 
				case "System.UInt64": return 8;
				case "System.IntPtr":
				case "System.nfloat":
				case "System.nuint":
				case "System.nint": return is_64_bits ? 8 : 4;
				default:
					int size = 0;
					foreach (FieldDefinition field in type.Fields) {
						if (field.IsStatic)
							continue;
						int s = GetValueTypeSize (field.FieldType.Resolve (), is_64_bits);
						if (s == -1)
							return -1;
						size += s;
					}
					return size;
			}
		}

		protected override int GetValueTypeSize (TypeReference type)
		{
			return GetValueTypeSize (type.Resolve (), Is64Bits);
		}

		protected override bool HasReleaseAttribute (MethodDefinition method)
		{
			CustomAttribute attrib;
			method = GetBaseMethodInTypeHierarchy (method);
			return SharedStatic.TryGetAttributeImpl (method.MethodReturnType, ObjCRuntime, StringConstants.ReleaseAttribute, out attrib);
		}

		protected override bool HasThisAttribute (MethodDefinition method)
		{
			CustomAttribute attrib;
			return SharedStatic.TryGetAttributeImpl (method, "System.Runtime.CompilerServices", "ExtensionAttribute", out attrib);
		}

#if MTOUCH
		public bool IsSimulator {
			get { return App.IsSimulatorBuild; }
		}
#endif

		protected override bool IsSimulatorOrDesktop {
			get {
#if MONOMAC
				return true;
#else
				return App.IsSimulatorBuild;
#endif
			}
		}
			
		protected override bool Is64Bits {
			get {
				if (IsSingleAssembly)
					return App.Is64Build;
				return Target.Is64Build;
			}
		}

		protected override bool IsDualBuildImpl {
			get {
#if MMP
				return Xamarin.Bundler.Driver.IsUnified;
#else
				return true;
#endif
			}
		}

		protected override Exception CreateException (int code, Exception innerException, MethodDefinition method, string message, params object[] args)
		{
			return ErrorHelper.CreateError (App, code, innerException, method, message, args);
		}

		protected override Exception CreateException (int code, Exception innerException, TypeReference type, string message, params object [] args)
		{
			return ErrorHelper.CreateError (App, code, innerException, type, message, args);
		}

		protected override bool ContainsPlatformReference (AssemblyDefinition assembly)
		{
			if (assembly.Name.Name == PlatformAssembly)
				return true;

			if (!assembly.MainModule.HasAssemblyReferences)
				return false;

			foreach (var ar in assembly.MainModule.AssemblyReferences) {
				if (ar.Name == PlatformAssembly)
					return true;
			}

			return false;
		}

		protected override IEnumerable<TypeReference> CollectTypes (AssemblyDefinition assembly)
		{
			var queue = new Queue<TypeDefinition> ();

			foreach (TypeDefinition type in assembly.MainModule.Types) {
				if (type.HasNestedTypes)
					queue.Enqueue (type);
				else
					yield return type;
			}

			while (queue.Count > 0) {
				var nt = queue.Dequeue ();
				foreach (var tp in nt.NestedTypes) {
					if (tp.HasNestedTypes)
						queue.Enqueue (tp);
					else
						yield return tp;
				}

				yield return nt;
			}
		}

		protected override IEnumerable<MethodDefinition> CollectMethods (TypeReference tr)
		{
			var type = tr.Resolve ();
			if (!type.HasMethods)
				yield break;

			foreach (MethodDefinition method in type.Methods) {
				if (method.IsConstructor)
					continue;

				yield return method;
			}
		}

		protected override IEnumerable<MethodDefinition> CollectConstructors (TypeReference tr)
		{
			var type = tr.Resolve ();
			if (!type.HasMethods)
				yield break;

			foreach (MethodDefinition ctor in type.Methods) {
				if (ctor.IsConstructor)
					yield return ctor;
			}
		}

		protected override IEnumerable<PropertyDefinition> CollectProperties (TypeReference tr)
		{
			var type = tr.Resolve ();
			if (!type.HasProperties)
				yield break;

			foreach (PropertyDefinition property in type.Properties)
				yield return property;
		}

		protected override string GetAssemblyName (AssemblyDefinition assembly)
		{
			return assembly.Name.Name;
		}

		protected override string GetTypeFullName (TypeReference type)
		{
			return type.FullName;
		}

		protected override string GetMethodName (MethodDefinition method)
		{
			return method.Name;
		}

		protected override string GetPropertyName (PropertyDefinition property)
		{
			return property.Name;
		}

		protected override TypeReference GetPropertyType (PropertyDefinition property)
		{
			return property.PropertyType;
		}

		protected override string GetTypeName (TypeReference type)
		{
			return type.Name;
		}

		protected override string GetFieldName (FieldDefinition field)
		{
			return field.Name;
		}

		protected override IEnumerable<FieldDefinition> GetFields (TypeReference tr)
		{
			var type = tr.Resolve ();
			foreach (FieldDefinition field in type.Fields)
				yield return field;
		}

		protected override TypeReference GetFieldType (FieldDefinition field)
		{
			return field.FieldType;
		}

		protected override bool IsByRef (TypeReference type)
		{
			return type.IsByReference;
		}

		protected override TypeReference MakeByRef (TypeReference type)
		{
			return new ByReferenceType (type);
		}

		protected override bool IsStatic (FieldDefinition field)
		{
			return field.IsStatic;
		}

		protected override bool IsStatic (MethodDefinition method)
		{
			return method.IsStatic;
		}
		
		protected override bool IsStatic (PropertyDefinition property)
		{
			if (property.GetMethod != null)
				return property.GetMethod.IsStatic;
			if (property.SetMethod != null)
				return property.SetMethod.IsStatic;
			return false;
		}

		protected override TypeReference GetElementType (TypeReference type)
		{
			return type.GetElementType ();
		}

		protected override TypeReference GetReturnType (MethodDefinition method)
		{
			return method.ReturnType;
		}

		TypeReference system_void;
		protected override TypeReference GetSystemVoidType ()
		{
			if (system_void != null)
				return system_void;
			
			// find corlib
			AssemblyDefinition corlib = null;
			AssemblyDefinition first = null;

			foreach (var assembly in input_assemblies) {
				if (first == null)
					first = assembly;
				if (assembly.Name.Name == "mscorlib") {
					corlib = assembly;
					break;
				}
			}

			if (corlib == null) {
				corlib = first.MainModule.AssemblyResolver.Resolve (AssemblyNameReference.Parse ("mscorlib"), new ReaderParameters ());
			}
			foreach (var type in corlib.MainModule.Types) {
				if (type.Namespace == "System" && type.Name == "Void")
					return system_void = type;
			}

			throw ErrorHelper.CreateError (4165, "The registrar couldn't find the type 'System.Void' in any of the referenced assemblies.");
		}

		protected override bool IsVirtual (MethodDefinition method)
		{
			return method.IsVirtual;
		}

		bool IsOverride (MethodDefinition method)
		{
			return method.IsVirtual && !method.IsNewSlot;
		}

		bool IsOverride (PropertyDefinition property)
		{
			if (property.GetMethod != null)
				return IsOverride (property.GetMethod);
			return IsOverride (property.SetMethod);
		}

		protected override bool IsConstructor (MethodDefinition method)
		{
			return method.IsConstructor;
		}

		protected override bool IsDelegate (TypeReference tr)
		{
			var type = tr.Resolve ();
			return SharedStatic.IsDelegate (type);
		}

		protected override bool IsValueType (TypeReference type)
		{
			var td = type.Resolve ();
			return td != null && td.IsValueType;
		}

		bool IsNativeEnum (TypeDefinition td)
		{
			return IsDualBuild && SharedStatic.HasAttribute (td, ObjCRuntime, StringConstants.NativeAttribute);
		}

		protected override bool IsEnum (TypeReference tr, out bool isNativeEnum)
		{
			var type = tr.Resolve ();
			isNativeEnum = false;
			if (type == null)
				return false;
			if (type.IsEnum)
				isNativeEnum = IsNativeEnum (type);
			return type.IsEnum;
		}

		protected override bool IsArray (TypeReference type)
		{
			return type is ArrayType;
		}

		protected override bool IsGenericType (TypeReference type)
		{
			return type is GenericInstanceType || type.HasGenericParameters || type is GenericParameter;
		}

		protected override bool IsGenericMethod (MethodDefinition method)
		{
			return method.HasGenericParameters;
		}

		protected override bool IsInterface (TypeReference type)
		{
			return type.Resolve ().IsInterface;
		}

		protected override TypeReference[] GetInterfaces (TypeReference type)
		{
			var td = type.Resolve ();
			if (!td.HasInterfaces || td.Interfaces.Count == 0)
				return null;
			var rv = new TypeReference [td.Interfaces.Count];
			for (int i = 0; i < td.Interfaces.Count; i++)
				rv [i] = td.Interfaces [i].InterfaceType.Resolve ();
			return rv;
		}

		protected override TypeReference GetGenericTypeDefinition (TypeReference type)
		{
			var git = type as GenericInstanceType;
			if (git != null)
				return git.ElementType;
			return type;
		}

		protected override bool VerifyIsConstrainedToNSObject (TypeReference type, out TypeReference constrained_type)
		{
			constrained_type = null;

			var gp = type as GenericParameter;
			if (gp != null) {
				if (!gp.HasConstraints)
					return false;
				foreach (var c in gp.Constraints) {
					if (IsNSObject (c)) {
						constrained_type = c;
						return true;
					}
				}
				return false;
			}

			var git = type as GenericInstanceType;
			if (git != null) {
				var rv = true;
				if (git.HasGenericArguments) {
					var newGit = new GenericInstanceType (git.ElementType);
					for (int i = 0; i < git.GenericArguments.Count; i++) {
						TypeReference constr;
						rv &= VerifyIsConstrainedToNSObject (git.GenericArguments [i], out constr);
						newGit.GenericArguments.Add (constr ?? git.GenericArguments [i]);
					}
					constrained_type = newGit;
				}
				return rv;
			}

			var el = type as ArrayType;
			if (el != null) {
				var rv = VerifyIsConstrainedToNSObject (el.ElementType, out constrained_type);
				if (constrained_type == null)
					return rv;
				constrained_type = new ArrayType (constrained_type, el.Rank);
				return rv;
			}

			var rt = type as ByReferenceType;
			if (rt != null) {
				var rv = VerifyIsConstrainedToNSObject (rt.ElementType, out constrained_type);
				if (constrained_type == null)
					return rv;
				constrained_type = new ByReferenceType (constrained_type);
				return rv;
			}

			var tr = type as PointerType;
			if (tr != null) {
				var rv = VerifyIsConstrainedToNSObject (tr.ElementType, out constrained_type);
				if (constrained_type == null)
					return rv;
				constrained_type = new PointerType (constrained_type);
				return rv;
			}

			return true;
		}

		protected override bool IsINativeObject (TypeReference tr)
		{
			return SharedStatic.IsNativeObject (tr);
		}

		protected override TypeReference GetBaseType (TypeReference tr)
		{
			var gp = tr as GenericParameter;
			if (gp != null) {
				foreach (var constr in gp.Constraints) {
					if (constr.Resolve ().IsClass) {
						return constr;
					}
				}
				return null;
			}
			var type = tr.Resolve ();
			if (type.BaseType == null)
				return null;

			return type.BaseType.Resolve ();
		}

		protected override MethodDefinition GetBaseMethod (MethodDefinition method)
		{
			return GetBaseMethodInTypeHierarchy (method);
		}

		protected override TypeReference GetEnumUnderlyingType (TypeReference tr)
		{
			var type = tr.Resolve ();
			return SharedStatic.GetEnumUnderlyingType (type);
		}

		protected override TypeReference[] GetParameters (MethodDefinition method)
		{
			if (!method.HasParameters || method.Parameters.Count == 0)
				return null;

			var types = new TypeReference [method.Parameters.Count];
			for (int i = 0; i < types.Length; i++)
				types [i] = method.Parameters [i].ParameterType;
			return types;
		}

		protected override string GetParameterName (MethodDefinition method, int parameter_index)
		{
			if (method == null)
				return "?";
			return method.Parameters [parameter_index].Name;
		}

		protected override MethodDefinition GetGetMethod (PropertyDefinition property)
		{
			return property.GetMethod;
		}

		protected override MethodDefinition GetSetMethod (PropertyDefinition property)
		{
			return property.SetMethod;
		}

		protected override void GetNamespaceAndName (TypeReference type, out string @namespace, out string name)
		{
			name = type.Name;
			@namespace = type.Namespace;
		}

		protected override bool TryGetAttribute (TypeReference type, string attributeNamespace, string attributeType, out object attribute)
		{
			CustomAttribute attrib;
			bool res = SharedStatic.TryGetAttributeImpl (type.Resolve (), attributeNamespace, attributeType, out attrib);
			attribute = attrib;
			return res;
		}

		public override RegisterAttribute GetRegisterAttribute (TypeReference type)
		{
			CustomAttribute attrib;
			RegisterAttribute rv = null;

			if (!SharedStatic.TryGetAttributeImpl (type.Resolve (), Foundation, StringConstants.RegisterAttribute, out attrib))
				return null;

			if (!attrib.HasConstructorArguments) {
				rv = new RegisterAttribute ();
			} else {
				switch (attrib.ConstructorArguments.Count) {
				case 0:
					rv = new RegisterAttribute ();
					break;
				case 1:
					rv = new RegisterAttribute ((string) attrib.ConstructorArguments [0].Value);
					break;
				case 2:
					rv = new RegisterAttribute ((string) attrib.ConstructorArguments [0].Value, (bool) attrib.ConstructorArguments [1].Value);
					break;
				default:
					throw ErrorHelper.CreateError (4124, "Invalid RegisterAttribute found on '{0}'. Please file a bug report at http://bugzilla.xamarin.com", type.FullName);
				}
			}

			if (attrib.HasProperties) {
				foreach (var prop in attrib.Properties) {
					switch (prop.Name) {
					case "IsWrapper":
						rv.IsWrapper = (bool) prop.Argument.Value;
						break;
					case "Name":
						rv.Name = (string) prop.Argument.Value;
						break;
					case "SkipRegistration":
						rv.SkipRegistration = (bool) prop.Argument.Value;
						break;
					default:
						throw ErrorHelper.CreateError (4124, "Invalid RegisterAttribute property {1} found on '{0}'. Please file a bug report at http://bugzilla.xamarin.com", type.FullName, prop.Name);
					}
				}
			}

			return rv;
		}

		protected override CategoryAttribute GetCategoryAttribute (TypeReference type)
		{
			CustomAttribute attrib;
			string name = null;

			if (!SharedStatic.TryGetAttributeImpl (type.Resolve (), ObjCRuntime, StringConstants.CategoryAttribute, out attrib))
				return null;

			if (!attrib.HasConstructorArguments)
				throw ErrorHelper.CreateError (4124, "Invalid CategoryAttribute found on '{0}'. Please file a bug report at http://bugzilla.xamarin.com", type.FullName);

			if (attrib.HasProperties) {
				foreach (var prop in attrib.Properties) {
					if (prop.Name == "Name") {
						name = (string) prop.Argument.Value;
						break;
					}
				}
			}

			switch (attrib.ConstructorArguments.Count) {
			case 1:
				var t1 = (TypeReference) attrib.ConstructorArguments [0].Value;
				return new CategoryAttribute (t1 != null ? t1.Resolve () : null) { Name = name };
			default:
				throw ErrorHelper.CreateError (4124, "Invalid CategoryAttribute found on '{0}'. Please file a bug report at http://bugzilla.xamarin.com", type.FullName);
			}
		}

		protected override ExportAttribute GetExportAttribute (MethodDefinition method)
		{
			return CreateExportAttribute (GetBaseMethodInTypeHierarchy (method) ?? method);
		}

		protected override ExportAttribute GetExportAttribute (PropertyDefinition property)
		{
			return CreateExportAttribute (GetBasePropertyInTypeHierarchy (property) ?? property);
		}

		protected override ProtocolAttribute GetProtocolAttribute (TypeReference type)
		{
			CustomAttribute attrib;

			if (!SharedStatic.TryGetAttributeImpl (type.Resolve (), Foundation, StringConstants.ProtocolAttribute, out attrib))
				return null;

			if (!attrib.HasProperties)
				return new ProtocolAttribute ();

			var rv = new ProtocolAttribute ();
			foreach (var prop in attrib.Properties) {
				switch (prop.Name) {
				case "Name":
					rv.Name = (string) prop.Argument.Value;
					break;
				case "WrapperType":
					rv.WrapperType = (TypeDefinition) prop.Argument.Value;
					break;
				case "IsInformal":
					rv.IsInformal = (bool) prop.Argument.Value;
					break;
				default:
					throw ErrorHelper.CreateError (4147, "Invalid {0} found on '{1}'. Please file a bug report at http://bugzilla.xamarin.com", "ProtocolAttribute", type.FullName);
				}
			}

			return rv;
		}

		protected override string PlatformName {
			get {
				return App.PlatformName;
			}
		}

		protected override IEnumerable<ProtocolMemberAttribute> GetProtocolMemberAttributes (TypeReference type)
		{
			var td = type.Resolve ();

			if (!td.HasCustomAttributes)
				yield break;

			foreach (var ca in td.CustomAttributes) {
				if (!ca.Constructor.DeclaringType.Is (Foundation, StringConstants.ProtocolMemberAttribute))
					continue;

				var rv = new ProtocolMemberAttribute ();
				foreach (var prop in ca.Properties) {
					switch (prop.Name) {
					case "IsRequired":
						rv.IsRequired = (bool)prop.Argument.Value;
						break;
					case "IsProperty":
						rv.IsProperty = (bool)prop.Argument.Value;
						break;
					case "IsStatic":
						rv.IsStatic = (bool)prop.Argument.Value;
						break;
					case "Name":
						rv.Name = (string)prop.Argument.Value;
						break;
					case "Selector":
						rv.Selector = (string)prop.Argument.Value;
						break;
					case "ReturnType":
						rv.ReturnType = (TypeReference)prop.Argument.Value;
						break;
					case "ParameterType":
						if (prop.Argument.Value != null) {
							var arr = (CustomAttributeArgument[])prop.Argument.Value;
							rv.ParameterType = new TypeReference[arr.Length];
							for (int i = 0; i < arr.Length; i++) {
								rv.ParameterType [i] = (TypeReference)arr [i].Value;
							}
						}
						break;
					case "ParameterByRef":
						if (prop.Argument.Value != null) {
							var arr = (CustomAttributeArgument[])prop.Argument.Value;
							rv.ParameterByRef = new bool[arr.Length];
							for (int i = 0; i < arr.Length; i++) {
								rv.ParameterByRef [i] = (bool)arr [i].Value;
							}
						}
						break;
					case "IsVariadic":
						rv.IsVariadic = (bool)prop.Argument.Value;
						break;
					case "PropertyType":
						rv.PropertyType = (TypeReference)prop.Argument.Value;
						break;
					case "GetterSelector":
						rv.GetterSelector = (string)prop.Argument.Value;
						break;
					case "SetterSelector":
						rv.SetterSelector = (string)prop.Argument.Value;
						break;
					case "ArgumentSemantic":
						rv.ArgumentSemantic = (ArgumentSemantic)prop.Argument.Value;
						break;
					}
				}
				yield return rv;
			}
		}

		void CollectAvailabilityAttributes (IEnumerable<CustomAttribute> attributes, ref List<AvailabilityBaseAttribute> list)
		{
			PlatformName currentPlatform;
#if MTOUCH
			switch (App.Platform) {
			case Xamarin.Utils.ApplePlatform.iOS:
				currentPlatform = global::XamCore.ObjCRuntime.PlatformName.iOS;
				break;
			case Xamarin.Utils.ApplePlatform.TVOS:
				currentPlatform = global::XamCore.ObjCRuntime.PlatformName.TvOS;
				break;
			case Xamarin.Utils.ApplePlatform.WatchOS:
				currentPlatform = global::XamCore.ObjCRuntime.PlatformName.WatchOS;
				break;
			default:
				throw ErrorHelper.CreateError (71, "Unknown platform: {0}. This usually indicates a bug in Xamarin.iOS; please file a bug report at http://bugzilla.xamarin.com with a test case.", App.Platform);
			}
#else
			currentPlatform = global::XamCore.ObjCRuntime.PlatformName.MacOSX;
#endif

			foreach (var ca in attributes) {
				var caType = ca.Constructor.DeclaringType.Resolve ();
				if (caType.Namespace != ObjCRuntime)
					continue;
				
				AvailabilityKind kind;
				PlatformName platformName;
				PlatformArchitecture architecture;
				string message = null;
				int majorVersion = 0, minorVersion = 0, subminorVersion = 0;

				switch (caType.Name) {
				case "IntroducedAttribute":
					kind = AvailabilityKind.Introduced;
					break;
				case "DeprecatedAttribute":
					kind = AvailabilityKind.Deprecated;
					break;
				case "ObsoletedAttribute":
					kind = AvailabilityKind.Obsoleted;
					break;
				case "UnavailableAttribute":
					kind = AvailabilityKind.Unavailable;
					break;
				default:
					continue;
				}

				switch (ca.ConstructorArguments.Count) {
				case 3:
					platformName = (PlatformName) ca.ConstructorArguments [0].Value;
					architecture = (PlatformArchitecture) ca.ConstructorArguments [1].Value;
					message = (string) ca.ConstructorArguments [2].Value;
					break;
				case 5:
					platformName = (PlatformName) ca.ConstructorArguments [0].Value;
					majorVersion = (int) ca.ConstructorArguments [1].Value;
					minorVersion = (int) ca.ConstructorArguments [2].Value;
					architecture = (PlatformArchitecture) ca.ConstructorArguments [3].Value;
					message = (string) ca.ConstructorArguments [4].Value;
					break;
				case 6:
					platformName = (PlatformName) ca.ConstructorArguments [0].Value;
					majorVersion = (int) ca.ConstructorArguments [1].Value;
					minorVersion = (int) ca.ConstructorArguments [2].Value;
					subminorVersion = (int) ca.ConstructorArguments [3].Value;
					architecture = (PlatformArchitecture) ca.ConstructorArguments [4].Value;
					message = (string) ca.ConstructorArguments [5].Value;
					break;
				default:
					throw ErrorHelper.CreateError (4163, "Internal error in the registrar ({0} ctor with {1} arguments). Please file a bug report at http://bugzilla.xamarin.com", caType.Name, ca.ConstructorArguments.Count);
				}

				if (platformName != currentPlatform)
					continue;

				AvailabilityBaseAttribute rv;
				switch (kind) {
				case AvailabilityKind.Introduced:
					switch (ca.ConstructorArguments.Count) {
					case 3:
						rv = new IntroducedAttribute (platformName, architecture, message);
						break;
					case 5:
						rv = new IntroducedAttribute (platformName, majorVersion, minorVersion, architecture, message);
						break;
					case 6:
						rv = new IntroducedAttribute (platformName, majorVersion, minorVersion, subminorVersion, architecture, message);
						break;
					default:
						throw ErrorHelper.CreateError (4163, "Internal error in the registrar ({0} ctor with {1} arguments). Please file a bug report at http://bugzilla.xamarin.com", caType.Name, ca.ConstructorArguments.Count);
					}
					break;
				case AvailabilityKind.Deprecated:
					switch (ca.ConstructorArguments.Count) {
					case 3:
						rv = new DeprecatedAttribute (platformName, architecture, message);
						break;
					case 5:
						rv = new DeprecatedAttribute (platformName, majorVersion, minorVersion, architecture, message);
						break;
					case 6:
						rv = new DeprecatedAttribute (platformName, majorVersion, minorVersion, subminorVersion, architecture, message);
						break;
					default:
						throw ErrorHelper.CreateError (4163, "Internal error in the registrar ({0} ctor with {1} arguments). Please file a bug report at http://bugzilla.xamarin.com", caType.Name, ca.ConstructorArguments.Count);
					}
					break;
				case AvailabilityKind.Obsoleted:
					switch (ca.ConstructorArguments.Count) {
					case 3:
						rv = new ObsoletedAttribute (platformName, architecture, message);
						break;
					case 5:
						rv = new ObsoletedAttribute (platformName, majorVersion, minorVersion, architecture, message);
						break;
					case 6:
						rv = new ObsoletedAttribute (platformName, majorVersion, minorVersion, subminorVersion, architecture, message);
						break;
					default:
						throw ErrorHelper.CreateError (4163, "Internal error in the registrar ({0} ctor with {1} arguments). Please file a bug report at http://bugzilla.xamarin.com", caType.Name, ca.ConstructorArguments.Count);
					}
					break;
				case AvailabilityKind.Unavailable:
					rv = new UnavailableAttribute (platformName, architecture, message);
					break;
				default:
					throw ErrorHelper.CreateError (4163, "Internal error in the registrar (Unknown availability kind: {0}). Please file a bug report at http://bugzilla.xamarin.com", kind);
				}

				if (list == null)
					list = new List<AvailabilityBaseAttribute> ();
				list.Add (rv);
			}
		}

		protected override List<AvailabilityBaseAttribute> GetAvailabilityAttributes (TypeReference obj)
		{
			TypeDefinition td = obj.Resolve ();
			List<AvailabilityBaseAttribute> rv = null;

			if (td == null)
				return null;
			
			if (td.HasCustomAttributes)
				CollectAvailabilityAttributes (td.CustomAttributes, ref rv);
			
			if (availability_annotations != null) {
				object attribObjects;
				if (availability_annotations.TryGetValue (td, out attribObjects))
					CollectAvailabilityAttributes ((List<CustomAttribute>) attribObjects, ref rv);
			}

			return rv;
		}

		protected override Version GetSDKVersion ()
		{
			return App.SdkVersion;
		}

		protected override Dictionary<MethodDefinition, List<MethodDefinition>> PrepareMethodMapping (TypeReference type)
		{
			return SharedStatic.PrepareInterfaceMethodMapping (type);
		}

		protected override TypeReference GetProtocolAttributeWrapperType (TypeReference type)
		{
			CustomAttribute attrib;

			if (!SharedStatic.TryGetAttributeImpl (type.Resolve (), Foundation, StringConstants.ProtocolAttribute, out attrib))
				return null;

			if (attrib.HasProperties) {
				foreach (var prop in attrib.Properties) {
					if (prop.Name == "WrapperType")
						return (TypeReference) prop.Argument.Value;
				}
			}

			return null;
		}

		protected override ConnectAttribute GetConnectAttribute (PropertyDefinition property)
		{
			CustomAttribute attrib;

			if (!SharedStatic.TryGetAttributeImpl (property, Foundation, StringConstants.ConnectAttribute, out attrib))
				return null;

			if (!attrib.HasConstructorArguments)
				return new ConnectAttribute ();

			switch (attrib.ConstructorArguments.Count) {
			case 0: return new ConnectAttribute ();
			case 1: return new ConnectAttribute (((string) attrib.ConstructorArguments [0].Value));
			default:
				throw ErrorHelper.CreateError (4124, "Invalid ConnectAttribute found on '{0}.{1}'. Please file a bug report at http://bugzilla.xamarin.com", property.DeclaringType.FullName, property.Name);
			}
		}

		static ExportAttribute CreateExportAttribute (IMemberDefinition candidate)
		{
			bool is_variadic = false;
			var attribute = GetExportAttribute (candidate);

			if (attribute == null)
				return null;

			if (attribute.Constructor.Parameters.Count != attribute.ConstructorArguments.Count)
				throw ErrorHelper.CreateError (4124, "Invalid ExportAttribute found on '{0}.{1}'. Please file a bug report at http://bugzilla.xamarin.com", candidate.DeclaringType.FullName, candidate.Name);

			if (attribute.HasProperties) {
				foreach (var prop in attribute.Properties) {
					if (prop.Name == "IsVariadic") {
						is_variadic = (bool) prop.Argument.Value;
						break;
					}
				}
			}

			if (!attribute.HasConstructorArguments)
				return new ExportAttribute (null) { IsVariadic = is_variadic };

			switch (attribute.ConstructorArguments.Count) {
			case 0:
				return new ExportAttribute (null) { IsVariadic = is_variadic };
			case 1:
				return new ExportAttribute ((string) attribute.ConstructorArguments [0].Value) { IsVariadic = is_variadic };
			case 2:
				return new ExportAttribute ((string) attribute.ConstructorArguments [0].Value, (ArgumentSemantic) attribute.ConstructorArguments [1].Value) { IsVariadic = is_variadic };
			default:
				throw ErrorHelper.CreateError (4124, "Invalid ExportAttribute found on '{0}.{1}'. Please file a bug report at http://bugzilla.xamarin.com", candidate.DeclaringType.FullName, candidate.Name);
			}
		}

		// [Export] is not sealed anymore - so we cannot simply compare strings
		static CustomAttribute GetExportAttribute (ICustomAttributeProvider candidate)
		{
			if (!candidate.HasCustomAttributes)
				return null;
			
			foreach (CustomAttribute ca in candidate.CustomAttributes) {
				if (ca.Constructor.DeclaringType.Inherits (Foundation, StringConstants.ExportAttribute))
					return ca;
			}
			return null;
		}
		
		PropertyDefinition GetBasePropertyInTypeHierarchy (PropertyDefinition property)
		{
			if (!IsOverride (property))
				return property;

			var @base = GetBaseType (property.DeclaringType);
			while (@base != null) {
				PropertyDefinition base_property = TryMatchProperty (@base.Resolve (), property);
				if (base_property != null)
					return GetBasePropertyInTypeHierarchy (base_property) ?? base_property;
				
				@base = GetBaseType (@base);
			}

			return property;
		}
		
		static PropertyDefinition TryMatchProperty (TypeDefinition type, PropertyDefinition property)
		{
			if (!type.HasProperties)
				return null;
			
			foreach (PropertyDefinition candidate in type.Properties)
				if (PropertyMatch (candidate, property))
					return candidate;
			
			return null;
		}
		
		static bool PropertyMatch (PropertyDefinition candidate, PropertyDefinition property)
		{
			if (candidate.Name != property.Name)
				return false;
			
			if (candidate.GetMethod != null) {
				if (property.GetMethod == null)
					return false;
				if (!SharedStatic.MethodMatch (candidate.GetMethod, property.GetMethod))
					return false;
			} else if (property.GetMethod != null) {
				return false;
			}
			
			if (candidate.SetMethod != null) {
				if (property.SetMethod == null)
					return false;
				if (!SharedStatic.MethodMatch (candidate.SetMethod, property.SetMethod))
					return false;
			} else if (property.SetMethod != null) {
				return false;
			}
			
			return true;
		}
		
		MethodDefinition GetBaseMethodInTypeHierarchy (MethodDefinition method)
		{
			if (!IsOverride (method))
				return method;

			var @base = GetBaseType (method.DeclaringType);
			while (@base != null) {
				MethodDefinition base_method = TryMatchMethod (@base.Resolve (), method);
				if (base_method != null)
					return GetBaseMethodInTypeHierarchy (base_method) ?? base_method;
				
				@base = GetBaseType (@base);
			}
			
			return method;
		}
		
		static MethodDefinition TryMatchMethod (TypeDefinition type, MethodDefinition method)
		{
			if (!type.HasMethods)
				return null;
			
			foreach (MethodDefinition candidate in type.Methods)
				if (SharedStatic.MethodMatch (candidate, method))
					return candidate;
			
			return null;
		}

		// here we try to create a specialized trampoline for the specified method.
		static int counter = 0;
		static bool trace = false;
		AutoIndentStringBuilder header;
		AutoIndentStringBuilder declarations; // forward declarations, struct definitions
		AutoIndentStringBuilder methods; // c methods that contain the actual implementations
		AutoIndentStringBuilder interfaces; // public objective-c @interface declarations
		AutoIndentStringBuilder nslog_start = new AutoIndentStringBuilder ();
		AutoIndentStringBuilder nslog_end = new AutoIndentStringBuilder ();
		
		AutoIndentStringBuilder comment = new AutoIndentStringBuilder ();
		AutoIndentStringBuilder copyback = new AutoIndentStringBuilder ();
		AutoIndentStringBuilder invoke = new AutoIndentStringBuilder ();
		AutoIndentStringBuilder setup_call_stack = new AutoIndentStringBuilder ();
		AutoIndentStringBuilder setup_return = new AutoIndentStringBuilder ();
		AutoIndentStringBuilder body = new AutoIndentStringBuilder ();
		AutoIndentStringBuilder body_setup = new AutoIndentStringBuilder ();
		
		HashSet<string> trampoline_names = new HashSet<string> ();
		HashSet<string> namespaces = new HashSet<string> ();
		HashSet<string> structures = new HashSet<string> ();

		Dictionary<Body, Body> bodies = new Dictionary<Body, Body> ();

		AutoIndentStringBuilder full_token_references = new AutoIndentStringBuilder ();
		uint full_token_reference_count;
		List<string> registered_assemblies = new List<string> ();

		bool IsPlatformType (TypeReference type)
		{
			if (type.IsNested)
				return false;

			var aname = type.Module.Assembly.Name.Name;
			if (aname != PlatformAssembly)
				return false;
				
			if (IsDualBuild) {
				return Driver.GetFrameworks (App).ContainsKey (type.Namespace);
			} else {
				return type.Namespace.StartsWith (CompatNamespace + ".", StringComparison.Ordinal);
			}
		}

		void CheckNamespace (ObjCType objctype,  List<Exception> exceptions)
		{
			CheckNamespace (objctype.Type, exceptions);
		}

		HashSet<string> reported_frameworks;
		void CheckNamespace (TypeReference type, List<Exception> exceptions)
		{
			if (!IsPlatformType (type))
				return;

			var ns = type.Namespace;

			Framework framework;
			if (Driver.GetFrameworks (App).TryGetValue (ns, out framework)) {
				if (framework.Version > App.SdkVersion) {
					if (reported_frameworks == null)
						reported_frameworks = new HashSet<string> ();
					if (!reported_frameworks.Contains (framework.Name)) {
						exceptions.Add (ErrorHelper.CreateError (4134, 
#if MMP
							"Your application is using the '{0}' framework, which isn't included in the {3} SDK you're using to build your app (this framework was introduced in {3} {2}, while you're building with the {3} {1} SDK.) " +
							"This configuration is not supported with the static registrar (pass --registrar:dynamic as an additional mmp argument in your project's Mac Build option to select). " +
							"Alternatively select a newer SDK in your app's Mac Build options.",
#else
							"Your application is using the '{0}' framework, which isn't included in the {3} SDK you're using to build your app (this framework was introduced in {3} {2}, while you're building with the {3} {1} SDK.) " +
							"Please select a newer SDK in your app's {3} Build options.",
#endif
							framework.Name, App.SdkVersion, framework.Version, App.PlatformName));
						reported_frameworks.Add (framework.Name);
					}
					return;
				}
			}

			// Strip off the 'MonoTouch.' prefix
			if (!IsDualBuild)
				ns = type.Namespace.Substring (ns.IndexOf ('.') + 1);
			
			CheckNamespace (ns, exceptions);
		}

		void CheckNamespace (string ns, List<Exception> exceptions)
		{
			if (string.IsNullOrEmpty (ns))
				return;

			if (namespaces.Contains (ns))
				return;
			
			namespaces.Add (ns);

			string h;
			switch (ns) {
#if MMP
			case "CoreBluetooth":
				header.WriteLine ("#import <IOBluetooth/IOBluetooth.h>");
				header.WriteLine ("#import <CoreBluetooth/CoreBluetooth.h>");
				return;
			case "CoreImage":
				h = "<QuartzCore/QuartzCore.h>";
				break;
			case "PdfKit":
			case "ImageKit":
			case "QuartzComposer":
			case "QuickLookUI":
				h = "<Quartz/Quartz.h>";
				break;
#endif
			case "OpenGLES":
				return;
			case "CoreAnimation":
				header.WriteLine ("#import <QuartzCore/QuartzCore.h>");
#if MTOUCH
				if (App.SdkVersion.Major > 7)
					header.WriteLine ("#import <QuartzCore/CAEmitterBehavior.h>");
#endif
				return;
			case "CoreMidi":	
				h = "<CoreMIDI/CoreMIDI.h>";
				break;
#if MTOUCH
			case "CoreTelephony":
				// Grr, why doesn't CoreTelephony have one header that includes the rest !?
				header.WriteLine ("#import <CoreTelephony/CoreTelephonyDefines.h>");
				header.WriteLine ("#import <CoreTelephony/CTCall.h>");
				header.WriteLine ("#import <CoreTelephony/CTCallCenter.h>");
				header.WriteLine ("#import <CoreTelephony/CTCarrier.h>");
				header.WriteLine ("#import <CoreTelephony/CTTelephonyNetworkInfo.h>");
				if (App.SdkVersion.Major >= 7) {
					header.WriteLine ("#import <CoreTelephony/CTSubscriber.h>");
					header.WriteLine ("#import <CoreTelephony/CTSubscriberInfo.h>");
				}
				return;
#endif
#if MTOUCH
			case "Accounts":
				var compiler = Path.GetFileName (App.CompilerPath);
				if (compiler == "gcc" || compiler == "g++") {
					exceptions.Add (new MonoTouchException (4121, true, "Cannot use GCC/G++ to compile the generated code from the static registrar when using the Accounts framework (the header files provided by Apple used during the compilation require Clang). Either use Clang (--compiler:clang) or the dynamic registrar (--registrar:dynamic)."));
					return;
				}
				goto default;
#endif
			case "CoreAudioKit":
				// fatal error: 'CoreAudioKit/CoreAudioKit.h' file not found
				// radar filed with Apple - but that framework / header is not yet shipped with the iOS SDK simulator
#if MTOUCH
				if (IsSimulator)
					return;
#endif
				goto default;
			case "Metal":
			case "MetalKit":
			case "MetalPerformanceShaders": // https://trello.com/c/mrjkAO9U/518-metal-simulator
				// #error Metal Simulator is currently unsupported
				// this framework is _officially_ not available on the simulator (in iOS8)
#if !MONOMAC
				if (IsSimulator)
					return;
#endif
				goto default;
			case "GameKit":
#if !MONOMAC
				if (IsSimulator && App.Platform == Xamarin.Utils.ApplePlatform.WatchOS)
					return; // No headers provided for watchOS/simulator.
#endif
				goto default;
			case "WatchKit":
				// There's a bug in Xcode 7 beta 2 headers where the build fails with
				// ObjC++ files if WatchKit.h is included before UIKit.h (radar 21651022).
				// Workaround this by manually include UIKit.h before WatchKit.h.
				if (!namespaces.Contains ("UIKit"))
					header.WriteLine ("#import <UIKit/UIKit.h>");
				header.WriteLine ("#import <WatchKit/WatchKit.h>");
				namespaces.Add ("UIKit");
				return;
			case "QTKit":
#if MONOMAC
				if (App.SdkVersion >= MacOSTenTwelveVersion)
					return; // 10.12 removed the header files for QTKit
#endif
				goto default;
			default:
				h = string.Format ("<{0}/{0}.h>", ns);
				break;
			}
			header.WriteLine ("#import {0}", h);
		}
		
		string CheckStructure (TypeDefinition structure, string descriptiveMethodName, MemberReference inMember)
		{
			string n;
			StringBuilder name = new StringBuilder ();
			var body = new AutoIndentStringBuilder (1);
			int size = 0;
			
			ProcessStructure (name, body, structure, ref size, descriptiveMethodName, structure, inMember);
			
			n = "struct trampoline_struct_" + name.ToString ();
			if (!structures.Contains (n)) {
				structures.Add (n);
				declarations.WriteLine ("{0} {{\n{1}}};", n, body.ToString ());
			}
			
			return n;
		}
		
		void ProcessStructure (StringBuilder name, AutoIndentStringBuilder body, TypeDefinition structure, ref int size, string descriptiveMethodName, TypeDefinition root_structure, MemberReference inMember)
		{
			switch (structure.FullName) {
			case "System.Char":
				name.Append ('s');
				body.AppendLine ("short v{0};", size);
				size += 1;
				break;
			case "System.Boolean": // map managed 'bool' to ObjC BOOL
				name.Append ('B');
				body.AppendLine ("BOOL v{0};", size);
				size += 1;
				break;
			case "System.Byte":
			case "System.SByte":
				name.Append ('b');
				body.AppendLine ("char v{0};", size);
				size += 1;
				break;
			case "System.UInt16":
			case "System.Int16":
				name.Append ('s');
				body.AppendLine ("short v{0};", size);
				size += 2;
				break;
			case "System.UInt32":
			case "System.Int32":
				name.Append ('i');
				body.AppendLine ("int v{0};", size);
				size += 4;
				break;
			case "System.Int64":
			case "System.UInt64":
				name.Append ('l');
				body.AppendLine ("long long v{0};", size);
				size += 8;
				break;
			case "System.Single":
				name.Append ('f');
				body.AppendLine ("float v{0};", size);
				size += 4;
				break;
			case "System.Double":
				name.Append ('d');
				body.AppendLine ("double v{0};", size);
				size += 8;
				break;
			case "System.IntPtr":
				name.Append ('p');
				body.AppendLine ("void *v{0};", size);
				size += 4; // for now at least...
				break;
			default:
				bool found = false;
				foreach (FieldDefinition field in structure.Fields) {
					if (field.IsStatic)
						continue;
					var fieldType = field.FieldType.Resolve ();
					if (fieldType == null) 
						throw ErrorHelper.CreateError (App, 4111, inMember, "The registrar cannot build a signature for type `{0}' in method `{1}`.", structure.FullName, descriptiveMethodName);
					if (!fieldType.IsValueType)
						throw ErrorHelper.CreateError (App, 4161, inMember, "The registrar found an unsupported structure '{0}': All fields in a structure must also be structures (field '{1}' with type '{2}' is not a structure).", root_structure.FullName, field.Name, fieldType.FullName);
					found = true;
					ProcessStructure (name, body, fieldType, ref size, descriptiveMethodName, root_structure, inMember);
				}
				if (!found)
					throw ErrorHelper.CreateError (App, 4111, inMember, "The registrar cannot build a signature for type `{0}' in method `{1}`.", structure.FullName, descriptiveMethodName);
				break;
			}
		}

		string GetUniqueTrampolineName (string suggestion)
		{
			char []fixup = suggestion.ToCharArray ();
			for (int i = 0; i < fixup.Length; i++) {
				char c = fixup [i];
				if (c >= 'a' && c <= 'z')
					continue;
				if (c >= 'A' && c <= 'Z')
					continue;
				if (c >= '0' && c <= '9')
					continue;
				fixup [i] = '_';
			}
			suggestion = new string (fixup);
			
			if (trampoline_names.Contains (suggestion)) {
				string tmp;
				int counter = 0;
				do {
					tmp = suggestion + (++counter).ToString ();
				} while (trampoline_names.Contains (tmp));
				suggestion = tmp;
			}
			
			trampoline_names.Add (suggestion);
			
			return suggestion;
		}

		string ToObjCParameterType (TypeReference type, string descriptiveMethodName, List<Exception> exceptions, MemberReference inMethod)
		{
			TypeDefinition td = type.Resolve ();
			var reftype = type as ByReferenceType;
			ArrayType arrtype = type as ArrayType;
			GenericParameter gp = type as GenericParameter;

			if (gp != null)
				return "id";

			if (reftype != null) {
				string res = ToObjCParameterType (reftype.GetElementType (), descriptiveMethodName, exceptions, inMethod);
				if (res == null)
					return null;
				return res + "*";
			}

			if (arrtype != null)
				return "NSArray *";

			var git = type as GenericInstanceType;
			if (git != null && IsNSObject (type)) {
				var sb = new StringBuilder ();
				var elementType = git.GetElementType ();

				sb.Append (ToObjCParameterType (elementType, descriptiveMethodName, exceptions, inMethod));

				if (sb [sb.Length - 1] != '*') {
					// I'm not sure if this is possible to hit (I couldn't come up with a test case), but better safe than sorry.
					AddException (ref exceptions, CreateException (4166, inMethod.Resolve () as MethodDefinition, "Cannot register the method '{0}' because the signature contains a type ({1}) that isn't a reference type.", descriptiveMethodName, GetTypeFullName (elementType)));
					return "id";
				}

				sb.Length--; // remove the trailing * of the element type

				sb.Append ('<');
				for (int i = 0; i < git.GenericArguments.Count; i++) {
					if (i > 0)
						sb.Append (", ");
					var argumentType = git.GenericArguments [i];
					if (!IsINativeObject (argumentType)) {
						// I believe the generic constraints we have should make this error impossible to hit, but better safe than sorry.
						AddException (ref exceptions, CreateException (4167, inMethod.Resolve () as MethodDefinition, "Cannot register the method '{0}' because the signature contains a generic type ({1}) with a generic argument type that doesn't implement INativeObject ({2}).", descriptiveMethodName, GetTypeFullName (type), GetTypeFullName (argumentType)));
						return "id";
					}
					sb.Append (ToObjCParameterType (argumentType, descriptiveMethodName, exceptions, inMethod));
				}
				sb.Append ('>');

				sb.Append ('*'); // put back the * from the element type

				return sb.ToString ();
			}

			switch (td.FullName) {
#if MMP
			case "System.Drawing.RectangleF": return "NSRect";
			case "System.Drawing.PointF": return "NSPoint";
			case "System.Drawing.SizeF": return "NSSize";
#else
			case "System.Drawing.RectangleF": return "CGRect";
			case "System.Drawing.PointF": return "CGPoint";
			case "System.Drawing.SizeF": return "CGSize";
#endif
			case "System.String": return "NSString *";
			case "System.IntPtr": return "void *";
			case "System.SByte": return "signed char";
			case "System.Byte": return "unsigned char";
			case "System.Char": return "signed short";
			case "System.Int16": return "short";
			case "System.UInt16": return "unsigned short";
			case "System.Int32": return "int";
			case "System.UInt32": return "unsigned int";
			case "System.Int64": return "long long";
			case "System.UInt64": return "unsigned long long";
			case "System.Single": return "float";
			case "System.Double": return "double";
			case "System.Boolean": return "BOOL"; // map managed 'bool' to ObjC BOOL = unsigned char
			case "System.Void": return "void";
			case "System.nint":
				CheckNamespace ("Foundation", exceptions);
				return "NSInteger";
			case "System.nuint":
				CheckNamespace ("Foundation", exceptions);
				return "NSUInteger";
			case "System.nfloat":
				CheckNamespace ("CoreGraphics", exceptions);
				return "CGFloat";
			case "System.DateTime":
				throw ErrorHelper.CreateError (4102, "The registrar found an invalid type `{0}` in signature for method `{2}`. Use `{1}` instead.", "System.DateTime", IsDualBuild ? "Foundation.NSDate" : CompatNamespace + ".Foundation.NSDate", descriptiveMethodName);
			case "ObjCRuntime.Selector":
			case CompatNamespace + ".ObjCRuntime.Selector": return "SEL";
			case "ObjCRuntime.Class":
			case CompatNamespace + ".ObjCRuntime.Class": return "Class";
			default:
				if (IsNSObject (td)) {
					if (!IsPlatformType (td))
						return "id";

					if (HasProtocolAttribute (td)) {
						return "id<" + GetExportedTypeName (td) + ">";
					} else {
						return GetExportedTypeName (td) + " *";
					}
				} else if (td.IsEnum) {
					if (IsDualBuild && SharedStatic.HasAttribute (td, ObjCRuntime, StringConstants.NativeAttribute)) {
						switch (SharedStatic.GetEnumUnderlyingType (td).FullName) {
						case "System.Int64":
							return "NSInteger";
						case "System.UInt64":
							return "NSUInteger";
						default:
							exceptions.Add (ErrorHelper.CreateError (4145, "Invalid enum '{0}': enums with the [Native] attribute must have a underlying enum type of either 'long' or 'ulong'.", td.FullName));
							return "NSInteger";
						}
					}

					return ToObjCParameterType (SharedStatic.GetEnumUnderlyingType (td), descriptiveMethodName, exceptions, inMethod);
				} else if (td.IsValueType) {
					if (IsPlatformType (td)) {
						CheckNamespace (td, exceptions);
						return td.Name;
					}
					return CheckStructure (td, descriptiveMethodName, inMethod);
				} else {
					return SharedStatic.ToObjCType (td);
				}
			}
		}
		
		string GetPrintfFormatSpecifier (TypeDefinition type, out bool unknown)
		{
			unknown = false;
			if (type.IsValueType) {
				switch (type.FullName) {
				case "System.Char": return "c";
				case "System.Boolean":
				case "System.SByte":
				case "System.Int16":
				case "System.Int32": return "i";
				case "System.Byte":
				case "System.UInt16":
				case "System.UInt32": return "u";
				case "System.Int64": return "lld";
				case "System.UInt64": return "llu";
				case "System.nint":	return "zd";
				case "System.nuint": return "tu";
				case "System.nfloat":
				case "System.Single":
				case "System.Double": return "f";
				default:
					unknown = true;
					return "p";
				}
			} else if (IsNSObject (type)) {
				return "@";
			} else {
				unknown = true;
				return "p";
			}
		}

		string GetObjCSignature (ObjCMethod method, List<Exception> exceptions)
		{
			if (method.CurrentTrampoline == Trampoline.Retain)
				return "-(id) retain";
			else if (method.CurrentTrampoline == Trampoline.Release)
				return "-(void) release";
			else if (method.CurrentTrampoline == Trampoline.GetGCHandle)
				return "-(int) xamarinGetGCHandle";
			else if (method.CurrentTrampoline == Trampoline.SetGCHandle)
				return "-(void) xamarinSetGCHandle: (int) gchandle";
#if MONOMAC
			else if (method.CurrentTrampoline == Trampoline.CopyWithZone1 || method.CurrentTrampoline == Trampoline.CopyWithZone2)
				return "-(id) copyWithZone: (NSZone *)zone";
#endif

			var sb = new StringBuilder ();
			var isCtor = method.CurrentTrampoline == Trampoline.Constructor;

			sb.Append ((method.IsStatic && !method.IsCategoryInstance) ? '+' : '-');
			sb.Append ('(');
			sb.Append (isCtor ? "id" : this.ToObjCParameterType (method.ReturnType, GetDescriptiveMethodName (method.Method), exceptions, method.Method));
			sb.Append (')');

			var split = method.Selector.Split (':');

			if (split.Length == 1) {
				sb.Append (' ');
				sb.Append (split [0]);
			} else {
				var indexOffset = method.IsCategoryInstance ? 1 : 0;
				for (int i = 0; i < split.Length - 1; i++) {
					sb.Append (' ');
					sb.Append (split [i]);
					sb.Append (':');
					sb.Append ('(');
					sb.Append (ToObjCParameterType (method.Parameters [i + indexOffset], method.DescriptiveMethodName, exceptions, method.Method));
					sb.Append (')');
					sb.AppendFormat ("p{0}", i);
				}
			}

			if (method.IsVariadic)
				sb.Append (", ...");

			return sb.ToString ();
		}

		void WriteFullName (StringBuilder sb, TypeReference type)
		{
			if (type.DeclaringType != null) {
				WriteFullName (sb, type.DeclaringType);
				sb.Append ('+');
			} else if (!string.IsNullOrEmpty (type.Namespace)) {
				sb.Append (type.Namespace);
				sb.Append ('.');
			}
			sb.Append (type.Name);
		}

		protected override string GetAssemblyQualifiedName (TypeReference type)
		{
			var gp = type as GenericParameter;
			if (gp != null)
				return gp.Name;

			var sb = new StringBuilder ();

			WriteFullName (sb, type);

			var git = type as GenericInstanceType;
			if (git != null) {
				sb.Append ('[');
				for (int i = 0; i < git.GenericArguments.Count; i++) {
					if (i > 0)
						sb.Append (',');
					sb.Append ('[');
					sb.Append (GetAssemblyQualifiedName (git.GenericArguments [i]));
					sb.Append (']');
				}
				sb.Append (']');
			}

			var td = type.Resolve ();
			if (td != null)
				sb.Append (", ").Append (td.Module.Assembly.Name.Name);

			return sb.ToString ();
		}

		static string EncodeNonAsciiCharacters (string value)
		{
			StringBuilder sb = null;
			for (int i = 0; i < value.Length; i++) {
				char c = value [i];
				if (c > 127) {
					if (sb == null) {
						sb = new StringBuilder (value.Length);
						sb.Append (value, 0, i);
					}
					sb.Append ("\\u");
					sb.Append (((int) c).ToString ("x4"));
				} else if (sb != null) {
					sb.Append (c);
				}
			}
			return sb != null ? sb.ToString () : value;
		}

		static bool IsTypeCore (ObjCType type, string nsToMatch)
		{
			var ns = type.Type.Namespace;

			var t = type.Type;
			while (string.IsNullOrEmpty (ns) && t.DeclaringType != null) {
				t = t.DeclaringType;
				ns = t.Namespace;
			}

			return ns == nsToMatch;
		}

		static bool IsQTKitType (ObjCType type) => IsTypeCore (type, "QTKit");
		static bool IsMapKitType (ObjCType type) => IsTypeCore (type, "MapKit");
		static bool IsIntentsType (ObjCType type) => IsTypeCore (type, "Intents");

		static bool IsMetalType (ObjCType type)
		{
			var ns = type.Type.Namespace;
			if (!IsDualBuild)
				ns = ns.Substring (CompatNamespace.Length + 1);

			switch (ns) {
			case "Metal":
			case "MetalKit":
			case "MetalPerformanceShaders":
				return true;
			default:
				return false;
			}
		}

		void Specialize (AutoIndentStringBuilder sb)
		{
			List<Exception> exceptions = new List<Exception> ();
			List<ObjCMember> skip = new List<ObjCMember> ();

			var map = new AutoIndentStringBuilder (1);
			var map_init = new AutoIndentStringBuilder ();

			var i = 0;
			
			map.AppendLine ("static MTClassMap __xamarin_class_map [] = {");
			if (string.IsNullOrEmpty (single_assembly)) {
				map_init.AppendLine ("void xamarin_create_classes () {");
			} else {
				map_init.AppendLine ("void xamarin_create_classes_{0} () {{", single_assembly.Replace ('.', '_').Replace ('-', '_'));
			}

			// Select the types that needs to be registered.
			var allTypes = new List<ObjCType> ();
			foreach (var @class in Types.Values) {
				if (!string.IsNullOrEmpty (single_assembly) && single_assembly != @class.Type.Module.Assembly.Name.Name)
					continue;

#if !MONOMAC
				var isPlatformType = IsPlatformType (@class.Type);
				if (isPlatformType && IsSimulatorOrDesktop && IsMetalType (@class))
					continue; // Metal isn't supported in the simulator.
#else
				// Don't register 64-bit only API on 32-bit XM
				if (!Is64Bits)
				{
					var attributes = GetAvailabilityAttributes (@class.Type); // Can return null list
					if (attributes != null && attributes.Any (x => x.Architecture == PlatformArchitecture.Arch64))
						continue;
				}

				if (IsQTKitType (@class) && App.SdkVersion >= MacOSTenTwelveVersion)
					continue; // QTKit header was removed in 10.12 SDK

				// These are 64-bit frameworks that extend NSExtensionContext / NSUserActivity, which you can't do
				// if the header doesn't declare them. So hack it away, since they are useless in 64-bit anyway
				if (!Is64Bits && (IsMapKitType (@class) || IsIntentsType (@class)))
					continue;
#endif

				
				if (@class.IsFakeProtocol)
					continue;

				allTypes.Add (@class);
			}

			// Move all the custom types to the end of the list, respecting 
			// existing order (so that a derived type always comes after
			// its base type; the Types.Values has that property, and we
			// need to keep it that way).

			var mappedEnd = allTypes.Count;
			var counter = 0;
			while (counter < mappedEnd) {
				if (!IsPlatformType (allTypes [counter].Type)) {
					var t = allTypes [counter];
					allTypes.RemoveAt (counter);
					allTypes.Add (t);
					mappedEnd--;
				} else {
					counter++;
				}
			}

			if (string.IsNullOrEmpty (single_assembly)) {
				foreach (var assembly in GetAssemblies ())
					registered_assemblies.Add (GetAssemblyName (assembly));
			} else {
				registered_assemblies.Add (single_assembly);
			}

			var customTypeCount = 0;
			foreach (var @class in allTypes) {
				var isPlatformType = IsPlatformType (@class.Type);

				skip.Clear ();

				if (!@class.IsProtocol && !@class.IsModel && !@class.IsCategory) {
					if (!isPlatformType)
						customTypeCount++;
					
					CheckNamespace (@class, exceptions);
					map.AppendLine ("{{ NULL, 0x{1:X} /* '{0}' => '{2}' */ }},", 
									@class.ExportedName,
									CreateTokenReference (@class.Type, TokenType.TypeDef), 
									GetAssemblyQualifiedName (@class.Type));

					bool use_dynamic;

					if (@class.Type.Resolve ().Module.Assembly.Name.Name == PlatformAssembly) {
						// we don't need to use the static ref to prevent the linker from removing (otherwise unreferenced) code for monotouch.dll types.
						use_dynamic = true; 
						// be smarter: we don't need to use dynamic refs for types available in the lowest version (target deployment) we building for.
						// We do need to use dynamic class lookup when the following conditions are all true:
						// * The class is not available in the target deployment version.
						// * The class is not in a weakly linked framework (for instance if an existing framework introduces a new class, we don't
						//   weakly link the framework because it already exists in the target deployment version - but since the class doesn't, we
						//   must use dynamic class lookup to determine if it's available or not.
					} else {
						use_dynamic = false;
					}

					switch (@class.ExportedName) {
					case "EKObject":
						// EKObject's class is a private symbol, so we can't link with it...
						use_dynamic = true;
						break;
					}

					string get_class;
					if (use_dynamic) {
						get_class = string.Format ("objc_getClass (\"{0}\")", @class.ExportedName);
					} else {
						get_class = string.Format ("[{0} class]", EncodeNonAsciiCharacters (@class.ExportedName));
					}

					map_init.AppendLine ("__xamarin_class_map [{1}].handle = {0};", get_class, i++);
				}

				if (@class.IsWrapper && isPlatformType)
					continue;

				if (@class.Methods == null && isPlatformType && !@class.IsProtocol && !@class.IsCategory)
					continue;

				if (@class.IsModel)
					continue;

				CheckNamespace (@class, exceptions);
				if (@class.BaseType != null)
					CheckNamespace (@class.BaseType, exceptions);
					
				var class_name = EncodeNonAsciiCharacters (@class.ExportedName);
				var is_protocol = @class.IsProtocol;

				// Publicly visible types should go into the header, private types go into the .m
				var td = @class.Type.Resolve ();
				AutoIndentStringBuilder iface;
				if (td.IsNotPublic || td.IsNestedPrivate || td.IsNestedAssembly || td.IsNestedFamilyAndAssembly) {
					iface = sb;
				} else {
					iface = interfaces;
				}
				
				if (@class.IsCategory) {
					var exportedName = EncodeNonAsciiCharacters (@class.BaseType.ExportedName);
					iface.Write ("@interface {0} ({1})", exportedName, @class.CategoryName);
					declarations.AppendFormat ("@class {0};\n", exportedName);
				} else if (is_protocol) {
					var exportedName = EncodeNonAsciiCharacters (@class.ProtocolName);
					iface.Write ("@protocol ").Write (exportedName);
					declarations.AppendFormat ("@protocol {0};\n", exportedName);
				} else {
					iface.Write ("@interface {0} : {1}", class_name, EncodeNonAsciiCharacters (@class.SuperType.ExportedName));
					declarations.AppendFormat ("@class {0};\n", class_name);
				}
				bool any_protocols = false;
				ObjCType tp = @class;
				while (tp != null && tp != tp.BaseType) {
					if (tp.IsWrapper)
						break; // no need to declare protocols for wrapper types, they do it already in their headers.
					if (tp.Protocols != null) {
						for (int p = 0; p < tp.Protocols.Length; p++) {
							if (tp.Protocols [p].ProtocolName == "UIAppearance")
								continue;
							iface.Append (any_protocols ? ", " : "<");
							any_protocols = true;
							iface.Append (tp.Protocols [p].ProtocolName);
							CheckNamespace (tp.Protocols [p], exceptions);
						}
					}
					tp = tp.BaseType;
				}
				if (any_protocols)
					iface.Append (">");

				AutoIndentStringBuilder implementation_fields = null;
				if (is_protocol) {
					iface.WriteLine ();
				} else {
					iface.WriteLine (" {");

					if (@class.Fields != null) {
						foreach (var field in @class.Fields.Values) {
							AutoIndentStringBuilder fields = null;
							if (field.IsPrivate) {
								// Private fields go in the @implementation section.
								if (implementation_fields == null)
									implementation_fields = new AutoIndentStringBuilder (1);
								fields = implementation_fields;
							} else {
								// Public fields go in the header.
								fields = iface;
							}
							try {
								switch (field.FieldType) {
								case "@":
									fields.Write ("id ");
									break;
								case "^v":
									fields.Write ("void *");
									break;
								case "XamarinObject":
									fields.Write ("XamarinObject ");
									break;
								default:
									throw ErrorHelper.CreateError (4120, "The registrar found an unknown field type '{0}' in field '{1}.{2}'. Please file a bug report at http://bugzilla.xamarin.com", 
										field.FieldType, field.DeclaringType.Type.FullName, field.Name);
								}
								fields.Write (field.Name);
								fields.WriteLine (";");
							} catch (Exception ex) {
								exceptions.Add (ex);
							}
						}
					}
					iface.WriteLine ("}");
				}

				iface.Indent ();
				if (@class.Properties != null) {
					foreach (var property in @class.Properties) {
						try {
							if (is_protocol)
								iface.Write (property.IsOptional ? "@optional " : "@required ");
							iface.Write ("@property (nonatomic");
							switch (property.ArgumentSemantic) {
							case ArgumentSemantic.Copy:
								iface.Write (", copy");
								break;
							case ArgumentSemantic.Retain:
								iface.Write (", retain");
								break;
							case ArgumentSemantic.Assign:
							case ArgumentSemantic.None:
							default:
								iface.Write (", assign");
								break;
							}
							if (property.IsReadOnly)
								iface.Write (", readonly");

							if (property.Selector != null) {
								if (property.GetterSelector != null && property.Selector != property.GetterSelector)
									iface.Write (", getter = ").Write (property.GetterSelector);
								if (property.SetterSelector != null) {
									var setterSel = string.Format ("set{0}{1}:", char.ToUpperInvariant (property.Selector [0]), property.Selector.Substring (1));
									if (setterSel != property.SetterSelector)
										iface.Write (", setter = ").Write (property.SetterSelector);
								}
							}

							iface.Write (") ");
							try {
								iface.Write (ToObjCParameterType (property.PropertyType, property.DeclaringType.Type.FullName, exceptions, property.Property));
							} catch (ProductException mte) {
								exceptions.Add (CreateException (4138, mte, property.Property, "The registrar cannot marshal the property type '{0}' of the property '{1}.{2}'.",
									GetTypeFullName (property.PropertyType), property.DeclaringType.Type.FullName, property.Name));
							}
							iface.Write (" ").Write (property.Selector);
							iface.WriteLine (";");
						} catch (Exception ex) {
							exceptions.Add (ex);
						}
					}
				}

				if (@class.Methods != null) {
					foreach (var method in @class.Methods) {
						try {
							if (is_protocol)
								iface.Write (method.IsOptional ? "@optional " : "@required ");
							iface.WriteLine ("{0};", GetObjCSignature (method, exceptions));
						} catch (ProductException ex) {
							skip.Add (method);
							exceptions.Add (ex);
						} catch (Exception ex) {
							skip.Add (method);
							exceptions.Add (ErrorHelper.CreateError (4114, ex, "Unexpected error in the registrar for the method '{0}.{1}' - Please file a bug report at http://bugzilla.xamarin.com", method.DeclaringType.Type.FullName, method.Method.Name));
						}
					}
				}
				iface.Unindent ();
				iface.WriteLine ("@end");
				iface.WriteLine ();

				if (!is_protocol && !@class.IsWrapper) {
					if (@class.IsCategory) {
						sb.WriteLine ("@implementation {0} ({1})", EncodeNonAsciiCharacters (@class.BaseType.ExportedName), @class.CategoryName);
					} else {
						sb.WriteLine ("@implementation {0} {{", class_name);
						if (implementation_fields != null) {
							sb.Indent ();
							sb.Append (implementation_fields);
							sb.Unindent ();
						}
						sb.WriteLine ("}");
					}
					sb.Indent ();
					if (@class.Methods != null) {
						foreach (var method in @class.Methods) {
							if (skip.Contains (method))
								continue;
							
							try {
								Specialize (sb, method, exceptions);
							} catch (Exception ex) {
								exceptions.Add (ex);
							}
						}
					}
					sb.Unindent ();
					sb.WriteLine ("@end");
				}
				sb.WriteLine ();
			}

			map.AppendLine ("{ NULL, 0 },");
			map.AppendLine ("};");
			map.AppendLine ();

			map.AppendLine ("static const char *__xamarin_registration_assemblies []= {");
			int count = 0;
			foreach (var assembly in registered_assemblies) {
				count++;
				if (count > 1)
					map.AppendLine (", ");
				map.Append ("\"");
				map.Append (assembly);
				map.Append ("\"");
			}
			map.AppendLine ();
			map.AppendLine ("};");
			map.AppendLine ();

			map.AppendLine ("static struct MTFullTokenReference __xamarin_token_references [] = {");
			map.AppendLine (full_token_references);
			map.AppendLine ("};");
			map.AppendLine ();

			map.AppendLine ("static struct MTRegistrationMap __xamarin_registration_map = {");
			map.AppendLine ("__xamarin_registration_assemblies,");
			map.AppendLine ("__xamarin_class_map,");
			map.AppendLine ("__xamarin_token_references,");
			map.AppendLine ("{0},", count);
			map.AppendLine ("{0},", i);
			map.AppendLine ("{0},", customTypeCount);
			map.AppendLine ("{0}", full_token_reference_count);
			map.AppendLine ("};");


			map_init.AppendLine ("xamarin_add_registration_map (&__xamarin_registration_map);");
			map_init.AppendLine ("}");

			sb.WriteLine (map.ToString ());
			sb.WriteLine (map_init.ToString ());

			if (exceptions.Count > 0)
				throw new AggregateException (exceptions);
		}

		static bool HasIntPtrBoolCtor (TypeDefinition type)
		{
			if (!type.HasMethods)
				return false;
			foreach (var method in type.Methods) {
				if (!method.IsConstructor || !method.HasParameters)
					continue;
				if (method.Parameters.Count != 2)
					continue;
				if (!method.Parameters [0].ParameterType.Is ("System", "IntPtr"))
					continue;
				if (method.Parameters [1].ParameterType.Is ("System", "Boolean"))
					return true;
			}
			return false;
		}

		void Specialize (AutoIndentStringBuilder sb, ObjCMethod method, List<Exception> exceptions)
		{
			var isGeneric = method.DeclaringType.IsGeneric;

			switch (method.CurrentTrampoline) {
			case Trampoline.Retain:
				sb.WriteLine ("-(id) retain");
				sb.WriteLine ("{");
				sb.WriteLine ("return xamarin_retain_trampoline (self, _cmd);");
				sb.WriteLine ("}");
				sb.WriteLine ();
				return;
			case Trampoline.Release:
				sb.WriteLine ("-(void) release");
				sb.WriteLine ("{");
				sb.WriteLine ("xamarin_release_trampoline (self, _cmd);");
				sb.WriteLine ("}");
				sb.WriteLine ();
				return;
			case Trampoline.GetGCHandle:
				sb.WriteLine ("-(int) xamarinGetGCHandle");
				sb.WriteLine ("{");
				sb.WriteLine ("return __monoObjectGCHandle.gc_handle;");
				sb.WriteLine ("}");
				sb.WriteLine ();
				return;
			case Trampoline.SetGCHandle:
				sb.WriteLine ("-(void) xamarinSetGCHandle: (int) gc_handle");
				sb.WriteLine ("{");
				sb.WriteLine ("__monoObjectGCHandle.gc_handle = gc_handle;");
				sb.WriteLine ("__monoObjectGCHandle.native_object = self;");
				sb.WriteLine ("}");
				sb.WriteLine ();
				return;
			case Trampoline.Constructor:
				if (isGeneric) {
					sb.WriteLine (GetObjCSignature (method, exceptions));
					sb.WriteLine ("{");
					sb.WriteLine ("xamarin_throw_product_exception (4126, \"Cannot construct an instance of the type '{0}' from Objective-C because the type is generic.\");\n", method.DeclaringType.Type.FullName.Replace ("/", "+"));
					sb.WriteLine ("return self;");
					sb.WriteLine ("}");
					return;
				}
				break;
#if MONOMAC
			case Trampoline.CopyWithZone1:
				sb.AppendLine ("-(id) copyWithZone: (NSZone *) zone");
				sb.AppendLine ("{");
				sb.AppendLine ("id rv;");
				sb.AppendLine ("int gchandle;");
				sb.AppendLine ();
				sb.AppendLine ("gchandle = xamarin_get_gchandle_with_flags (self);");
				sb.AppendLine ("if (gchandle != 0)");
				sb.Indent ().AppendLine ("xamarin_set_gchandle (self, 0);").Unindent ();
				// Call the base class implementation
				sb.AppendLine ("rv = [super copyWithZone: zone];");
				sb.AppendLine ();
				sb.AppendLine ("if (gchandle != 0)");
				sb.Indent ().AppendLine ("xamarin_set_gchandle (self, gchandle);").Unindent ();
				sb.AppendLine ();
				sb.AppendLine ("return rv;");
				sb.AppendLine ("}");
				return;
			case Trampoline.CopyWithZone2:
				sb.AppendLine ("-(id) copyWithZone: (NSZone *) zone");
				sb.AppendLine ("{");
				sb.AppendLine ("return xamarin_copyWithZone_trampoline2 (self, _cmd, zone);");
				sb.AppendLine ("}");
				return;
#endif
			}

			var rettype = string.Empty;
			var returntype = method.Method.ReturnType;
			var isStatic = method.IsStatic;
			var isInstanceCategory = method.IsCategoryInstance;
			var isCtor = false;
			var num_arg = method.Method.HasParameters ? method.Method.Parameters.Count : 0;
			var descriptiveMethodName = method.DescriptiveMethodName;
			var name = GetUniqueTrampolineName ("native_to_managed_trampoline_" + descriptiveMethodName);
			var isVoid = returntype.FullName == "System.Void";
			var merge_bodies = true;

			switch (method.CurrentTrampoline) {
			case Trampoline.None:
			case Trampoline.Normal:
			case Trampoline.Static:
			case Trampoline.Single:
			case Trampoline.Double:
			case Trampoline.Long:
			case Trampoline.StaticLong:
			case Trampoline.StaticDouble:
			case Trampoline.StaticSingle:
			case Trampoline.X86_DoubleABI_StaticStretTrampoline:
			case Trampoline.X86_DoubleABI_StretTrampoline:
			case Trampoline.StaticStret:
			case Trampoline.Stret:
				switch (returntype.FullName) {
				case "System.Int64":
					rettype = "long long";
					break;
				case "System.UInt64":
					rettype = "unsigned long long";
					break;
				case "System.Single":
					rettype = "float";
					break;
				case "System.Double":
					rettype = "double";
					break;
				default:
					rettype = ToObjCParameterType (returntype, descriptiveMethodName, exceptions, method.Method);
					break;
				}
				break;
			case Trampoline.Constructor:
				rettype = "id";
				isCtor = true;
				break;
			default:
				return;
			}

			comment.Clear ();
			nslog_start.Clear ();
			nslog_end.Clear ();
			copyback.Clear ();
			invoke.Clear ();
			setup_call_stack.Clear ();
			body.Clear ();
			body_setup.Clear ();
			setup_return.Clear ();
			
			counter++;

			body.WriteLine ("{");

			var indent = merge_bodies ? sb.Indentation : sb.Indentation + 1;
			body.Indentation = indent;
			body_setup.Indentation = indent;
			copyback.Indentation = indent;
			invoke.Indentation = indent;
			setup_call_stack.Indentation = indent;
			setup_return.Indentation = indent;

			var token_ref = CreateTokenReference (method.Method, TokenType.Method);

			// A comment describing the managed signature
			if (trace) {
				nslog_start.Indentation = sb.Indentation;
				comment.Indentation = sb.Indentation;
				nslog_end.Indentation = sb.Indentation;

				comment.AppendFormat ("// {2} {0}.{1} (", method.Method.DeclaringType.FullName, method.Method.Name, method.Method.ReturnType.FullName);
				for (int i = 0; i < num_arg; i++) {
					var param = method.Method.Parameters [i];
					if (i > 0)
						comment.Append (", ");
					comment.AppendFormat ("{0} {1}", param.ParameterType.FullName, param.Name);
				}
				comment.AppendLine (")");
				comment.AppendLine ("// ArgumentSemantic: {0} IsStatic: {1} Selector: '{2}' Signature: '{3}'", method.ArgumentSemantic, method.IsStatic, method.Selector, method.Signature);
			}
		
			// a couple of debug printfs
			if (trace) {
				StringBuilder args = new StringBuilder ();
				nslog_start.AppendFormat ("NSLog (@\"{0} (this: %@, sel: %@", name);
				for (int i = 0; i < num_arg; i++) {
					var type = method.Method.Parameters [i].ParameterType;
					bool isRef = type.IsByReference;
					if (isRef)
						type = type.GetElementType ();
					var td = type.Resolve ();
					
					nslog_start.AppendFormat (", {0}: ", method.Method.Parameters [i].Name);
					args.Append (", ");
					switch (type.FullName) {
					case "System.Drawing.RectangleF":
						if (isRef) {
							nslog_start.Append ("%p : %@");
#if MMP
							args.AppendFormat ("p{0}, p{0} ? NSStringFromRect (*p{0}) : @\"NULL\"", i);
#else
							args.AppendFormat ("p{0}, p{0} ? NSStringFromCGRect (*p{0}) : @\"NULL\"", i);
#endif
						} else {
							nslog_start.Append ("%@"); 
#if MMP
							args.AppendFormat ("NSStringFromRect (p{0})", i);
#else
							args.AppendFormat ("NSStringFromCGRect (p{0})", i);
#endif
						}
						break;
					case "System.Drawing.PointF":
						if (isRef) {
							nslog_start.Append ("%p: %@"); 
#if MMP
							args.AppendFormat ("p{0}, p{0} ? NSStringFromPoint (*p{0}) : @\"NULL\"", i);
#else
							args.AppendFormat ("p{0}, p{0} ? NSStringFromCGPoint (*p{0}) : @\"NULL\"", i);
#endif
						} else {
							nslog_start.Append ("%@"); 
#if MMP
							args.AppendFormat ("NSStringFromPoint (p{0})", i);
#else
							args.AppendFormat ("NSStringFromCGPoint (p{0})", i);
#endif
						}
						break;
					default:
						bool unknown;
						var spec = GetPrintfFormatSpecifier (td, out unknown);
						if (unknown) {
							nslog_start.AppendFormat ("%{0}", spec);
							args.AppendFormat ("&p{0}", i);
						} else if (isRef) {
							nslog_start.AppendFormat ("%p *= %{0}", spec);
							args.AppendFormat ("p{0}, *p{0}", i);
						} else {
							nslog_start.AppendFormat ("%{0}", spec);
							args.AppendFormat ("p{0}", i);
						}
						break;
					}
				}
				
				string ret_arg = string.Empty;
				nslog_end.Append (nslog_start.ToString ());
				if (!isVoid) {
					bool unknown;
					var spec = GetPrintfFormatSpecifier (method.Method.ReturnType.Resolve (), out unknown);
					if (!unknown) {
						nslog_end.Append (" ret: %");
						nslog_end.Append (spec);
						ret_arg = ", res";
					}
				}
				nslog_end.Append (") END\", self, NSStringFromSelector (_cmd)");
				nslog_end.Append (args.ToString ());
				nslog_end.Append (ret_arg);
				nslog_end.AppendLine (");");
				
				nslog_start.Append (") START\", self, NSStringFromSelector (_cmd)");
				nslog_start.Append (args.ToString ());
				nslog_start.AppendLine (");");
			}

			// prepare the parameters
			var baseMethod = GetBaseMethodInTypeHierarchy (method.Method);
			for (int i = 0; i < num_arg; i++) {
				var param = method.Method.Parameters [i];
				var paramBase = baseMethod.Parameters [i];
				var type = method.Parameters [i];
				var objctype = ToObjCParameterType (type, descriptiveMethodName, exceptions, method.Method);
				var original_objctype = objctype;
				var isRef = type.IsByReference;
				var isOut = param.IsOut || paramBase.IsOut;
				var isArray = type is ArrayType;
				var isNativeEnum = false;
				var td = type.Resolve ();
				var isVariadic = i + 1 == num_arg && method.IsVariadic;

				if (isRef) {
					type = type.GetElementType ();
					td = type.Resolve ();
					original_objctype = ToObjCParameterType (type, descriptiveMethodName, exceptions,  method.Method);
					objctype = ToObjCParameterType (type, descriptiveMethodName, exceptions, method.Method) + "*";
				} else if (td.IsEnum) {
					type = SharedStatic.GetEnumUnderlyingType (td);
					isNativeEnum = IsDualBuild && SharedStatic.HasAttribute (td, ObjCRuntime, StringConstants.NativeAttribute);
					td = type.Resolve ();
				}

				switch (type.FullName) {
				case "System.Int64":
				case "System.UInt64":
					// We already show MT4145 if the underlying enum type isn't a long or ulong
					if (isNativeEnum) {
						string tp;
						string ntp;
						if (type.FullName == "System.UInt64") {
							tp = "unsigned long long";
							ntp = "NSUInteger";
						} else {
							tp = "long long";
							ntp = "NSInteger";
						}

						if (isRef || isOut) {
							body_setup.AppendLine ("{1} nativeEnum{0} = 0;", i, tp);
							setup_call_stack.AppendLine ("arg_ptrs [{0}] = &nativeEnum{0};", i);
							copyback.AppendLine ("*p{0} = ({1}) nativeEnum{0};", ntp);
						} else {
							body_setup.AppendLine ("{1} nativeEnum{0} = p{0};", i, tp);
							setup_call_stack.AppendLine ("arg_ptrs [{0}] = &nativeEnum{0};", i);
						}
						break;
					}
					goto case "System.SByte";
				case "System.SByte":
				case "System.Byte":
				case "System.Char":
				case "System.Int16":
				case "System.UInt16":
				case "System.Int32":
				case "System.UInt32":
				case "System.Single":
				case "System.Double":
				case "System.Boolean":
					if (isRef || isOut) {
						// The isOut semantics isn't quite correct here: we pass the actual input value to managed code.
						// In theory we should create a temp location and then use a writeback when done instead.
						// This should be safe though, since managed code (at least C#) can't actually observe the value.
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = p{0};", i);
					} else {
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = &p{0};", i);
					}
					break;
				case "System.IntPtr":
					if (isVariadic) {
						body_setup.AppendLine ("va_list a{0};", i);
						setup_call_stack.AppendLine ("va_start (a{0}, p{1});", i, i - 1);
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = &a{0};", i);
						copyback.AppendLine ("va_end (a{0});", i);
					} else if (isOut) {
						body_setup.AppendLine ("{1} a{0} = 0;", i, objctype);
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = &a{0};", i);
						copyback.AppendLine ("*p{0} = a{0};", i);
					} else if (isRef) {
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = p{0};", i);
					} else {
						body_setup.AppendLine ("{1} a{0} = p{0};", i, original_objctype);
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = &a{0};", i);
					}
					break;
				case "ObjCRuntime.Selector":
				case CompatNamespace + ".ObjCRuntime.Selector":
					if (isRef) {
						if (isOut) {
							body_setup.AppendLine ("void *a{0} = NULL;", i);
						} else {
							body_setup.AppendLine ("void *a{0} = NULL;", i);
							setup_call_stack.AppendLine ("a{0} = *p{0} ? xamarin_get_selector (*p{0}, &exception_gchandle) : NULL;", i);
							setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
						}
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = &a{0};", i);
						copyback.AppendLine ("*p{0} = a{0};", i);
					} else {
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = p{0} ? xamarin_get_selector (p{0}, &exception_gchandle) : NULL;", i);
						setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
					}
					break;
				case "ObjCRuntime.Class":
				case CompatNamespace + ".ObjCRuntime.Class":
					if (isRef) {
						if (isOut) {
							body_setup.AppendLine ("void *a{0} = NULL;", i);
						} else {
							body_setup.AppendLine ("void *a{0} = NULL;", i);
							setup_call_stack.AppendLine ("a{0} = *p{0} ? xamarin_get_class (*p{0}, &exception_gchandle) : NULL;", i);
							setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
						}
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = &a{0};", i);
						copyback.AppendLine ("*p{0} = a{0};", i);
					} else {
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = p{0} ? xamarin_get_class (p{0}, &exception_gchandle) : NULL;", i);
						setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
					}
					break;
				case "System.String":
					// This should always be an NSString and never char*
					if (isRef) {
						body_setup.AppendLine ("MonoString *a{0} = NULL;", i);
						if (!isOut)
							setup_call_stack.AppendLine ("a{0} = *p{0} ? mono_string_new (mono_domain_get (), [(*p{0}) UTF8String]) : NULL;", i);
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = &a{0};", i);
						body_setup.AppendLine ("char *str{0} = NULL;", i);
						copyback.AppendLine ("str{0} = mono_string_to_utf8 (a{0});", i);
						copyback.AppendLine ("*p{0} = [[NSString alloc] initWithUTF8String:str{0}];", i);
						copyback.AppendLine ("[*p{0} autorelease];", i);
						copyback.AppendLine ("mono_free (str{0});", i);
					} else {
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = p{0} ? mono_string_new (mono_domain_get (), [p{0} UTF8String]) : NULL;", i);
					}
					break;
				default:
					if (isArray) {
						var elementType = ((ArrayType)type).ElementType;
						var isNativeObject = false;
						
						setup_call_stack.AppendLine ("if (p{0}) {{", i);
						setup_call_stack.AppendLine ("NSArray *arr = (NSArray *) p{0};", i);
						if (App.EnableDebug)
							setup_call_stack.AppendLine ("xamarin_check_objc_type (p{0}, [NSArray class], _cmd, self, {0}, managed_method);", i);
						setup_call_stack.AppendLine ("MonoClass *e_class;");
						setup_call_stack.AppendLine ("MonoArray *marr;");
						setup_call_stack.AppendLine ("MonoType *p;");
						setup_call_stack.AppendLine ("int j;", i);
						setup_call_stack.AppendLine ("p = xamarin_get_parameter_type (managed_method, {0});", i);
						setup_call_stack.AppendLine ("e_class = mono_class_get_element_class (mono_class_from_mono_type (p));");
						setup_call_stack.AppendLine ("marr = mono_array_new (mono_domain_get (), e_class, [arr count]);", i);
						setup_call_stack.AppendLine ("for (j = 0; j < [arr count]; j++) {{", i);
						if (elementType.FullName == "System.String") {
							setup_call_stack.AppendLine ("NSString *sv = (NSString *) [arr objectAtIndex: j];", i);
							setup_call_stack.AppendLine ("mono_array_set (marr, MonoString *, j, mono_string_new (mono_domain_get (), [sv UTF8String]));", i);
						} else if (IsNSObject (elementType) || (elementType.Namespace == "System" && elementType.Name == "Object") || (isNativeObject = SharedStatic.IsNativeObject (elementType))) {
							setup_call_stack.AppendLine ("NSObject *nobj = [arr objectAtIndex: j];");
							setup_call_stack.AppendLine ("MonoObject *mobj{0} = NULL;", i);
							setup_call_stack.AppendLine ("if (nobj) {");
							if (isNativeObject) {
								TypeDefinition nativeObjType = elementType.Resolve ();

								if (nativeObjType.IsInterface) {
									var wrapper_type = GetProtocolAttributeWrapperType (nativeObjType);
									if (wrapper_type == null)
										throw ErrorHelper.CreateError (4125, "The registrar found an invalid type '{0}' in signature for method '{1}': " +
											"The interface must have a Protocol attribute specifying its wrapper type.",
											td.FullName, descriptiveMethodName);

									nativeObjType = wrapper_type.Resolve ();
								}

								// verify that the type has a ctor with two parameters
								if (!HasIntPtrBoolCtor (nativeObjType))
									throw ErrorHelper.CreateError (4103, 
										"The registrar found an invalid type `{0}` in signature for method `{1}`: " + 
										"The type implements INativeObject, but does not have a constructor that takes " +
										"two (IntPtr, bool) arguments.", nativeObjType.FullName, descriptiveMethodName);


								if (nativeObjType.IsInterface) {
									setup_call_stack.AppendLine ("mobj{0} = xamarin_get_inative_object_static (nobj, false, \"{1}\", \"{2}\");", i, GetAssemblyQualifiedName (nativeObjType), GetAssemblyQualifiedName (elementType));
								} else {
									// find the MonoClass for this parameter
									setup_call_stack.AppendLine ("MonoType *type{0};", i);
									setup_call_stack.AppendLine ("type{0} = xamarin_get_parameter_type (managed_method, {0});", i);
									setup_call_stack.AppendLine ("mobj{0} = xamarin_get_inative_object_dynamic (nobj, false, mono_type_get_object (mono_domain_get (), mono_class_get_type (e_class)), &exception_gchandle);", i);
									setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
								}
							} else {
								setup_call_stack.AppendLine ("mobj{0} = xamarin_get_managed_object_for_ptr_fast (nobj, &exception_gchandle);", i);
								setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
							}
							if (App.EnableDebug) {
								setup_call_stack.AppendLine ("xamarin_verify_parameter (mobj{0}, _cmd, self, nobj, {0}, e_class, managed_method);", i);
							}
							setup_call_stack.AppendLine ("}");
							setup_call_stack.AppendLine ("mono_array_set (marr, MonoObject *, j, mobj{0});", i);
						} else {
							throw ErrorHelper.CreateError (App, 4111, method.Method, "The registrar cannot build a signature for type `{0}' in method `{1}`.", type.FullName, descriptiveMethodName);
						}
						setup_call_stack.AppendLine ("}");
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = marr;", i);
						setup_call_stack.AppendLine ("} else {");
						setup_call_stack.AppendLine ("arg_ptrs [{0}] = NULL;", i);
						setup_call_stack.AppendLine ("}");
					} else if (IsNSObject (type)) {
						if (isRef) {
							body_setup.AppendLine ("MonoObject *mobj{0} = NULL;", i);
							if (!isOut) {
								body_setup.AppendLine ("NSObject *nsobj{0} = NULL;", i);
								setup_call_stack.AppendLine ("nsobj{0} = *(NSObject **) p{0};", i);
								setup_call_stack.AppendLine ("if (nsobj{0}) {{", i);
								body_setup.AppendLine ("MonoType *paramtype{0} = NULL;", i);
								setup_call_stack.AppendLine ("paramtype{0} = xamarin_get_parameter_type (managed_method, {0});", i);
								setup_call_stack.AppendLine ("mobj{0} = xamarin_get_nsobject_with_type_for_ptr (nsobj{0}, false, paramtype{0}, &exception_gchandle);", i);
								setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
								if (App.EnableDebug) {
									setup_call_stack.AppendLine ("xamarin_verify_parameter (mobj{0}, _cmd, self, nsobj{0}, {0}, mono_class_from_mono_type (paramtype{0}), managed_method);", i);
								}
								setup_call_stack.AppendLine ("}");
							}

							// argument semantics?
							setup_call_stack.AppendLine ("arg_ptrs [{0}] = (int *) &mobj{0};", i);
							body_setup.AppendLine ("void * handle{0} = NULL;", i);
							copyback.AppendLine ("if (mobj{0} != NULL)", i);
							copyback.AppendLine ("handle{0} = xamarin_get_nsobject_handle (mobj{0});", i);
							copyback.AppendLine ("if (p{0} != NULL)", i).Indent ();
							copyback.AppendLine ("*p{0} = (id) handle{0};", i).Unindent ();
						} else {
							body_setup.AppendLine ("NSObject *nsobj{0} = NULL;", i);
							setup_call_stack.AppendLine ("nsobj{0} = (NSObject *) p{0};", i);
							if (method.ArgumentSemantic == ArgumentSemantic.Copy) {
								setup_call_stack.AppendLine ("nsobj{0} = [nsobj{0} copy];", i);
								setup_call_stack.AppendLine ("[nsobj{0} autorelease];", i);
							}
							body_setup.AppendLine ("MonoObject *mobj{0} = NULL;", i);
							body_setup.AppendLine ("int32_t created{0} = false;", i);
							setup_call_stack.AppendLine ("if (nsobj{0}) {{", i);
							body_setup.AppendLine ("MonoType *paramtype{0} = NULL;", i);
							setup_call_stack.AppendLine ("paramtype{0} = xamarin_get_parameter_type (managed_method, {0});", i);
							setup_call_stack.AppendLine ("mobj{0} = xamarin_get_nsobject_with_type_for_ptr_created (nsobj{0}, false, paramtype{0}, &created{0}, &exception_gchandle);", i);
							setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
							if (App.EnableDebug) {
								setup_call_stack.AppendLine ("xamarin_verify_parameter (mobj{0}, _cmd, self, nsobj{0}, {0}, mono_class_from_mono_type (paramtype{0}), managed_method);", i);
							}
							setup_call_stack.AppendLine ("}");
							setup_call_stack.AppendLine ("arg_ptrs [{0}] = mobj{0};", i);

							if (SharedStatic.HasAttribute (paramBase, ObjCRuntime, StringConstants.TransientAttribute)) {
								copyback.AppendLine ("if (created{0}) {{", i);
								copyback.AppendLine ("xamarin_dispose (mobj{0}, &exception_gchandle);", i);
								copyback.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
								copyback.AppendLine ("}");
							}
						}
					} else if (SharedStatic.IsNativeObject (td)) {
						TypeDefinition nativeObjType = td;

						if (td.IsInterface) {
							var wrapper_type = GetProtocolAttributeWrapperType (td);
							if (wrapper_type == null)
								throw ErrorHelper.CreateError (4125, "The registrar found an invalid type '{0}' in signature for method '{1}': " +
								                              "The interface must have a Protocol attribute specifying its wrapper type.",
								                              td.FullName, descriptiveMethodName);

							nativeObjType = wrapper_type.Resolve ();
						}

						// verify that the type has a ctor with two parameters
						if (!HasIntPtrBoolCtor (nativeObjType))
							throw ErrorHelper.CreateError (4103, 
							                              "The registrar found an invalid type `{0}` in signature for method `{1}`: " + 
								"The type implements INativeObject, but does not have a constructor that takes " +
							    "two (IntPtr, bool) arguments.", nativeObjType.FullName, descriptiveMethodName);

						if (!td.IsInterface) {
							// find the MonoClass for this parameter
							body_setup.AppendLine ("MonoType *type{0};", i);
							setup_call_stack.AppendLine ("type{0} = xamarin_get_parameter_type (managed_method, {0});", i);
						}
						if (isRef) {
							body_setup.AppendLine ("MonoObject *inobj{0} = NULL;", i);
							if (isOut) {
								setup_call_stack.AppendLine ("inobj{0} = NULL;", i);
							} else if (td.IsInterface) {
								setup_call_stack.AppendLine ("inobj{0} = xamarin_get_inative_object_static (*p{0}, false, \"{1}\", \"{2}\", &exception_gchandle);", i, GetAssemblyQualifiedName (nativeObjType), GetAssemblyQualifiedName (td));
								setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
							} else {
								setup_call_stack.AppendLine ("inobj{0} = xamarin_get_inative_object_dynamic (*p{0}, false, mono_type_get_object (mono_domain_get (), type{0}), &exception_gchandle);", i);
								setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
							}
							setup_call_stack.AppendLine ("arg_ptrs [{0}] = &inobj{0};", i);
							body_setup.AppendLine ("id handle{0} = nil;", i);
							copyback.AppendLine ("if (inobj{0} != NULL)", i);
							copyback.AppendLine ("handle{0} = xamarin_get_handle_for_inativeobject (inobj{0}, &exception_gchandle);", i);
							copyback.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
							copyback.AppendLine ("*p{0} = (id) handle{0};", i);
						} else {
							if (td.IsInterface) {
								setup_call_stack.AppendLine ("arg_ptrs [{0}] = xamarin_get_inative_object_static (p{0}, false, \"{1}\", \"{2}\", &exception_gchandle);", i, GetAssemblyQualifiedName (nativeObjType), GetAssemblyQualifiedName (td));
							} else {
								setup_call_stack.AppendLine ("arg_ptrs [{0}] = xamarin_get_inative_object_dynamic (p{0}, false, mono_type_get_object (mono_domain_get (), type{0}), &exception_gchandle);", i);
							}
							setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
						}
					} else if (type.IsValueType) {
						if (isRef || isOut) {
							// The isOut semantics isn't quite correct here: we pass the actual input value to managed code.
							// In theory we should create a temp location and then use a writeback when done instead.
							// This should be safe though, since managed code (at least C#) can't actually observe the value.
							setup_call_stack.AppendLine ("arg_ptrs [{0}] = p{0};", i);
						} else {
							setup_call_stack.AppendLine ("arg_ptrs [{0}] = &p{0};", i);
						}
					} else if (td.BaseType.FullName == "System.MulticastDelegate") {
						if (isRef) {
							throw ErrorHelper.CreateError (4110,
							                              "The registrar cannot marshal the out parameter of type `{0}` in signature for method `{1}`.",
							                              type.FullName, descriptiveMethodName);
						} else {
							// Bug #4858 (also related: #4718)
							setup_call_stack.AppendLine ("if (p{0}) {{", i);
							setup_call_stack.AppendLine ("arg_ptrs [{0}] = (void *) xamarin_get_delegate_for_block_parameter (managed_method, {0}, p{0}, &exception_gchandle);", i);
							setup_call_stack.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
							setup_call_stack.AppendLine ("} else {");
							setup_call_stack.AppendLine ("arg_ptrs [{0}] = NULL;", i);
							setup_call_stack.AppendLine ("}");
						}
					} else {
						throw ErrorHelper.CreateError (4105,
						                              "The registrar cannot marshal the parameter of type `{0}` in signature for method `{1}`.",
						                              type.FullName, descriptiveMethodName);
					}
					break;
				}
			}
			
			// the actual invoke
			if (isCtor) {
				invoke.AppendLine ("mthis = mono_object_new (mono_domain_get (), mono_method_get_class (managed_method));", counter);
				body_setup.AppendLine ("uint8_t flags = NSObjectFlagsNativeRef;");
				invoke.AppendLine ("xamarin_set_nsobject_handle (mthis, self);");
				invoke.AppendLine ("xamarin_set_nsobject_flags (mthis, flags);");
			}

			var marshal_exception = "NULL";
			if (App.MarshalManagedExceptions != MarshalManagedExceptionMode.Disable) {
				body_setup.AppendLine ("MonoObject *exception = NULL;");
				if (App.EnableDebug && App.IsDefaultMarshalManagedExceptionMode) {
					body_setup.AppendLine ("MonoObject **exception_ptr = xamarin_is_managed_exception_marshaling_disabled () ? NULL : &exception;");
					marshal_exception = "exception_ptr";
				} else {
					marshal_exception = "&exception";
				}
			}

			if (!isVoid) {
				body_setup.AppendLine ("MonoObject *retval = NULL;");
				invoke.Append ("retval = ");
			}

			invoke.AppendLine ("mono_runtime_invoke (managed_method, {0}, arg_ptrs, {1});", isStatic ? "NULL" : "mthis", marshal_exception);
		
			if (isCtor)
				invoke.AppendLine ("xamarin_create_managed_ref (self, mthis, true);");

			body_setup.AppendLine ("guint32 exception_gchandle = 0;");
			// prepare the return value
			if (!isVoid) {
				switch (rettype) {
				case "CGRect":
					body_setup.AppendLine ("{0} res = {{{{0}}}};", rettype);
					break;
				default:
					body_setup.AppendLine ("{0} res = {{0}};", rettype);
					break;
				}
				var isArray = returntype is ArrayType;
				var type = returntype.Resolve () ?? returntype;
				var retain = method.RetainReturnValue;

				if (returntype.IsValueType) {
					setup_return.AppendLine ("res = *({0} *) mono_object_unbox ((MonoObject *) retval);", rettype);
				} else if (isArray) {
					var elementType = ((ArrayType) returntype).ElementType;
					
					setup_return.AppendLine ("if (retval) {");
					setup_return.AppendLine ("int length = mono_array_length ((MonoArray *) retval);");
					setup_return.AppendLine ("int i;");
					setup_return.AppendLine ("id *buf = (id *) malloc (sizeof (void *) * length);");
					setup_return.AppendLine ("for (i = 0; i < length; i++) {");
					setup_return.AppendLine ("MonoObject *value = mono_array_get ((MonoArray *) retval, MonoObject *, i);");
					
					if (elementType.FullName == "System.String") {
						setup_return.AppendLine ("char *str = mono_string_to_utf8 ((MonoString *) value);");
						setup_return.AppendLine ("NSString *sv = [[NSString alloc] initWithUTF8String:str];");
						setup_return.AppendLine ("[sv autorelease];");
						setup_return.AppendLine ("mono_free (str);");
						setup_return.AppendLine ("buf [i] = sv;");
					} else if (IsNSObject (elementType)) {
						setup_return.AppendLine ("buf [i] = xamarin_get_nsobject_handle ((MonoObject *) value);");
					} else if (IsINativeObject (elementType)) {
						setup_return.AppendLine ("buf [i] = xamarin_get_handle_for_inativeobject ((MonoObject *) value, &exception_gchandle);");
						setup_return.AppendLine ("xamarin_process_managed_exception_gchandle (exception_gchandle);");
						setup_return.AppendLine ("if (exception_gchandle != 0) {");
						setup_return.AppendLine ("free (buf);");
						setup_return.AppendLine ("goto exception_handling;");
						setup_return.AppendLine ("}");
					} else {
						throw ErrorHelper.CreateError (App, 4111, method.Method, "The registrar cannot build a signature for type `{0}' in method `{1}`.", returntype.FullName, descriptiveMethodName);
					}
					
					setup_return.AppendLine ("}");
						
					setup_return.AppendLine ("NSArray *arr = [[NSArray alloc] initWithObjects: buf count: length];");
					setup_return.AppendLine ("free (buf);");
					if (!retain)
						setup_return.AppendLine ("[arr autorelease];");
					setup_return.AppendLine ("res = arr;");
					setup_return.AppendLine ("} else {");
					setup_return.AppendLine ("res = NULL;");
					setup_return.AppendLine ("}");
					setup_return.AppendLine ("xamarin_framework_peer_lock ();");
					setup_return.AppendLine ("mt_dummy_use (retval);");
					setup_return.AppendLine ("xamarin_framework_peer_unlock ();");
				} else {
					setup_return.AppendLine ("if (!retval) {");
					setup_return.AppendLine ("res = NULL;");
					setup_return.AppendLine ("} else {");

					if (IsNSObject (type)) {
						setup_return.AppendLine ("id retobj;");
						setup_return.AppendLine ("retobj = xamarin_get_nsobject_handle (retval);");
						setup_return.AppendLine ("xamarin_framework_peer_lock ();");
						setup_return.AppendLine ("[retobj retain];");
						setup_return.AppendLine ("xamarin_framework_peer_unlock ();");
						if (!retain)
							setup_return.AppendLine ("[retobj autorelease];");
						setup_return.AppendLine ("mt_dummy_use (retval);");
						setup_return.AppendLine ("res = retobj;");
					} else if (type.IsPlatformType ("ObjCRuntime", "Selector")) {
						setup_return.AppendLine ("res = xamarin_get_selector_handle (retval, &exception_gchandle);");
						setup_return.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
					} else if (type.IsPlatformType ("ObjCRuntime", "Class")) {
						setup_return.AppendLine ("res = xamarin_get_class_handle (retval, &exception_gchandle);");
						setup_return.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
					} else if (SharedStatic.IsNativeObject (type)) {
						setup_return.AppendLine ("{0} retobj;", rettype);
						setup_return.AppendLine ("retobj = xamarin_get_handle_for_inativeobject ((MonoObject *) retval, &exception_gchandle);");
						setup_return.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
						setup_return.AppendLine ("xamarin_framework_peer_lock ();");
						setup_return.AppendLine ("[retobj retain];");
						setup_return.AppendLine ("xamarin_framework_peer_unlock ();");
						if (!retain)
							setup_return.AppendLine ("[retobj autorelease];");
						setup_return.AppendLine ("mt_dummy_use (retval);");
						setup_return.AppendLine ("res = retobj;");
					} else if (type.FullName == "System.String") {
						// This should always be an NSString and never char*
						setup_return.AppendLine ("char *str = mono_string_to_utf8 ((MonoString *) retval);");
						setup_return.AppendLine ("NSString *nsstr = [[NSString alloc] initWithUTF8String:str];");
						if (!retain)
							setup_return.AppendLine ("[nsstr autorelease];");
						setup_return.AppendLine ("mono_free (str);");
						setup_return.AppendLine ("res = nsstr;");
					} else if (SharedStatic.IsDelegate (type.Resolve ())) {
						setup_return.AppendLine ("res = xamarin_get_block_for_delegate (managed_method, retval, &exception_gchandle);");
						setup_return.AppendLine ("if (exception_gchandle != 0) goto exception_handling;");
					} else {
						throw ErrorHelper.CreateError (4104, 
							"The registrar cannot marshal the return value for type `{0}` in signature for method `{1}`.",
							returntype.FullName, descriptiveMethodName);
					}

					setup_return.AppendLine ("}");
				}
			}

			if (App.Embeddinator)
				body.WriteLine ("xamarin_embeddinator_initialize ();");

			body.WriteLine ("MONO_ASSERT_GC_SAFE;");
			body.WriteLine ("MONO_THREAD_ATTACH;"); // COOP: this will switch to GC_UNSAFE
			body.WriteLine ();

			// Write out everything
			if (merge_bodies) {
				body_setup.WriteLine ("MonoMethod *managed_method = *managed_method_ptr;");
			} else {
				if (!isGeneric)
					body.Write ("static ");
				body.WriteLine ("MonoMethod *managed_method = NULL;");
			}

			if (comment.Length > 0)
				body.WriteLine (comment.ToString ());
			if (isInstanceCategory)
				body.WriteLine ("id p0 = self;");
			body_setup.WriteLine ("void *arg_ptrs [{0}];", num_arg);
			if (!isStatic || isInstanceCategory)
				body.WriteLine ("MonoObject *mthis = NULL;");
			
			if (isCtor) {
				body.WriteLine ("bool has_nsobject = xamarin_has_nsobject (self, &exception_gchandle);");
				body.WriteLine ("if (exception_gchandle != 0) goto exception_handling;");
				body.WriteLine ("if (has_nsobject) {");
				body.WriteLine ("*call_super = true;");
				body.WriteLine ("goto exception_handling;");
				body.WriteLine ("}");
			}

			if ((!isStatic || isInstanceCategory) && !isCtor) {
				body.WriteLine ("if (self) {");
				body.WriteLine ("mthis = xamarin_get_managed_object_for_ptr_fast (self, &exception_gchandle);");
				body.WriteLine ("if (exception_gchandle != 0) goto exception_handling;");
				body.WriteLine ("}");
			}

			// no locking should be required here, it doesn't matter if we overwrite the field (it'll be the same value).
			body.WriteLine ("if (!managed_method) {");
			body.Write ("MonoReflectionMethod *reflection_method = ");
			if (isGeneric)
				body.Write ("xamarin_get_generic_method_from_token (mthis, ");
			else
				body.Write ("xamarin_get_method_from_token (");

			if (merge_bodies) {
				body.WriteLine ("token_ref, &exception_gchandle);");
			} else {
				body.WriteLine ("0x{0:X}, &exception_gchandle);", token_ref);
			}
			body.WriteLine ("if (exception_gchandle != 0) goto exception_handling;");
			body.WriteLine ("managed_method = xamarin_get_reflection_method_method (reflection_method);");
			if (merge_bodies)
				body.WriteLine ("*managed_method_ptr = managed_method;");
			
			body.WriteLine ("}");

			if (!isStatic && !isInstanceCategory && !isCtor) {
				body.WriteLine ("xamarin_check_for_gced_object (mthis, _cmd, self, managed_method, &exception_gchandle);");
				body.WriteLine ("if (exception_gchandle != 0) goto exception_handling;");
			}

			if (trace)
				body.AppendLine (nslog_start);

			body.AppendLine (setup_call_stack);
			body.AppendLine (invoke);
			body.AppendLine (copyback);
			body.AppendLine (setup_return);
			
			if (trace )
				body.AppendLine (nslog_end);

			body.StringBuilder.AppendLine ("exception_handling:;");

			body.WriteLine ("MONO_THREAD_DETACH;"); // COOP: this will switch to GC_SAFE

			body.AppendLine ("if (exception_gchandle != 0)");
			body.Indent ().WriteLine ("xamarin_process_managed_exception_gchandle (exception_gchandle);").Unindent ();

			if (App.MarshalManagedExceptions != MarshalManagedExceptionMode.Disable)
				body.WriteLine ("xamarin_process_managed_exception (exception);");

			if (isCtor) {
				body.WriteLine ("return self;");
			} else if (isVoid) {
				body.WriteLine ("return;");
			} else {
				body.WriteLine ("return res;");
			}

			body.WriteLine ("}");

			body.StringBuilder.Insert (2, body_setup);

			/* We merge duplicated bodies (based on the signature of the method and the entire body) */

			var objc_signature = new StringBuilder ().Append (rettype).Append (":");
			if (method.Method.HasParameters) {
				for (int i = 0; i < method.Method.Parameters.Count; i++)
					objc_signature.Append (ToObjCParameterType (method.Method.Parameters [i].ParameterType, descriptiveMethodName, exceptions, method.Method)).Append (":");
			}

			Body existing;
			Body b = new Body () {
				Code = body.ToString (),
				Signature = objc_signature.ToString (),
			};

			if (merge_bodies && bodies.TryGetValue (b, out existing)) {
				/* We already have an identical trampoline, use it instead */
				b = existing;
			} else {
				/* Need to create a new trampoline */
				if (merge_bodies)
					bodies [b] = b;
				b.Name = "native_to_managed_trampoline_" + bodies.Count.ToString ();
				
				if (merge_bodies) {
					methods.Append ("static ");
					methods.Append (rettype).Append (" ").Append (b.Name).Append (" (id self, SEL _cmd, MonoMethod **managed_method_ptr");
					var pcount = method.Method.HasParameters ? method.Method.Parameters.Count : 0;
					for (int i = (isInstanceCategory ? 1 : 0); i < pcount; i++) {
						methods.Append (", ").Append (ToObjCParameterType (method.Method.Parameters [i].ParameterType, descriptiveMethodName, exceptions, method.Method));
						methods.Append (" ").Append ("p").Append (i.ToString ());
					}
					if (isCtor)
						methods.Append (", bool* call_super");
					methods.Append (", uint32_t token_ref");
					methods.AppendLine (")");
					methods.AppendLine (body);
					methods.AppendLine ();
				}
			}
			b.Count++;

			sb.WriteLine ();
			sb.WriteLine (GetObjCSignature (method, exceptions));
			if (merge_bodies) {
				sb.WriteLine ("{");
				if (!isGeneric)
					sb.Write ("static ");
				sb.WriteLine ("MonoMethod *managed_method = NULL;");
				if (isCtor) {
					sb.WriteLine ("bool call_super = false;");
					sb.Write ("id rv = ");
				} else if (!isVoid) {
					sb.Write ("return ");
				}
				sb.Write (b.Name);
				sb.Write (" (self, _cmd, &managed_method");
				var paramCount = method.Method.HasParameters ? method.Method.Parameters.Count : 0;
				if (isInstanceCategory)
					paramCount--;
				for (int i = 0; i < paramCount; i++)
					sb.Write (", p{0}", i);
				if (isCtor)
					sb.Write (", &call_super");
				sb.Write (", 0x{0:X}", token_ref);
				sb.WriteLine (");");
				if (isCtor) {
					sb.WriteLine ("if (call_super && rv) {");
					sb.Write ("struct objc_super super = {  rv, [").Write (method.DeclaringType.SuperType.ExportedName).WriteLine (" class] };");
					sb.Write ("rv = ((id (*)(objc_super*, SEL");

					if (method.Parameters != null) {
						for (int i = 0; i < method.Parameters.Length; i++)
							sb.Append (", ").Append (ToObjCParameterType (method.Parameters [i], method.DescriptiveMethodName, exceptions, method.Method));
					}
					if (method.IsVariadic)
						sb.Append (", ...");
				
					sb.Write (")) objc_msgSendSuper) (&super, @selector (");
					sb.Write (method.Selector);
					sb.Write (")");
					var split = method.Selector.Split (':');
					for (int i = 0; i < split.Length - 1; i++) {
						sb.Append (", ");
						sb.AppendFormat ("p{0}", i);
					}
					sb.WriteLine (");");
					sb.WriteLine ("}");
					sb.WriteLine ("return rv;");
				}
				sb.WriteLine ("}");
			} else {
				sb.WriteLine (body);
			}
		}

		class Body {
			public string Code;
			public string Signature;
			public string Name;
			public int Count;

			public override int GetHashCode ()
			{
				return Code.GetHashCode () ^ Signature.GetHashCode ();
			}
			public override bool Equals (object obj)
			{
				var other = obj as Body;
				if (other == null)
					return false;
				return Code == other.Code && Signature == other.Signature;
			}
		}

		uint CreateFullTokenReference (MemberReference member)
		{
			var rv = (full_token_reference_count++ << 1) + 1;
			full_token_references.AppendFormat ("\t\t{{ /* #{3} = 0x{4:X} */ \"{0}\", 0x{1:X}, 0x{2:X} }},\n", GetAssemblyName (member.Module.Assembly), member.Module.MetadataToken.ToUInt32 (), member.MetadataToken.ToUInt32 (), full_token_reference_count, rv);
			return rv;
		}

		uint CreateTokenReference (MemberReference member, TokenType implied_type)
		{
			var token = member.MetadataToken;

			/* We can't create small token references if we're in partial mode, because we may have multiple arrays of registered assemblies, and no way of saying which one we refer to with the assembly index */
			if (IsSingleAssembly)
				return CreateFullTokenReference (member);

			/* If the implied token type doesn't match, we need a full token */
			if (implied_type != token.TokenType)
				return CreateFullTokenReference (member);

			/* For small token references the only valid module is the first one */
			if (member.Module.MetadataToken.ToInt32 () != 1)
				return CreateFullTokenReference (member);

			/* The assembly must be a registered one, and only within the first 128 assemblies */
			var assembly_name = GetAssemblyName (member.Module.Assembly);
			var index = registered_assemblies.IndexOf (assembly_name);
			if (index < 0 || index > 127)
				return CreateFullTokenReference (member);

			return (token.RID << 8) + ((uint) index << 1);
		}

		public void GeneratePInvokeWrappersStart (AutoIndentStringBuilder hdr, AutoIndentStringBuilder decls, AutoIndentStringBuilder mthds, AutoIndentStringBuilder ifaces)
		{
			header = hdr;
			declarations = decls;
			methods = mthds;
			interfaces = ifaces;
		}

		public void GeneratePInvokeWrappersEnd ()
		{
			header = null;	
			declarations = null;
			methods = null;
			interfaces = null;
			namespaces.Clear ();
			structures.Clear ();

			FlushTrace ();
		}

		static string GetParamName (MethodDefinition method, int i)
		{
			var p = method.Parameters [i];
			if (p.Name != null)
				return p.Name;
			return "__p__" + i.ToString ();
		}

		public void GeneratePInvokeWrapper (PInvokeWrapperGenerator state, MethodDefinition method)
		{
			var signatures = state.signatures;
			var exceptions = state.exceptions;
			var signature = state.signature;
			var names = state.names;
			var sb = state.sb;
			var pinfo = method.PInvokeInfo;
			var is_stret = pinfo.EntryPoint.EndsWith ("_stret", StringComparison.Ordinal);
			var isVoid = method.ReturnType.FullName == "System.Void";
			var descriptiveMethodName = method.DeclaringType.FullName + "." + method.Name;

			signature.Clear ();

			string native_return_type;
			int first_parameter = 0;

			if (is_stret) {
				native_return_type = ToObjCParameterType (method.Parameters [0].ParameterType.GetElementType (), descriptiveMethodName, exceptions, method);
				first_parameter = 1;
			} else {
				native_return_type = ToObjCParameterType (method.ReturnType, descriptiveMethodName, exceptions, method);
			}

			signature.Append (native_return_type);
			signature.Append (" ");
			signature.Append (pinfo.EntryPoint);
			signature.Append (" (");
			for (int i = 0; i < method.Parameters.Count; i++) {
				if (i > 0)
					signature.Append (", ");
				signature.Append (ToObjCParameterType (method.Parameters[i].ParameterType, descriptiveMethodName, exceptions, method));
			}
			signature.Append (")");

			string wrapperName;
			if (!signatures.TryGetValue (signature.ToString (), out wrapperName)) {
				var name = "xamarin_pinvoke_wrapper_" + method.Name;
				var counter = 0;
				while (names.Contains (name)) {
					name = "xamarin_pinvoke_wrapper_" + method.Name + (++counter).ToString ();
				}
				names.Add (name);
				signatures [signature.ToString ()] = wrapperName = name;

				sb.WriteLine ("// EntryPoint: {0}", pinfo.EntryPoint);
				sb.WriteLine ("// Managed method: {0}.{1}", method.DeclaringType.FullName, method.Name);
				sb.WriteLine ("// Signature: {0}", signature.ToString ());

				sb.Write ("typedef ");
				sb.Write (native_return_type);
				sb.Write ("(*func_");
				sb.Write (name);
				sb.Write (") (");
				for (int i = first_parameter; i < method.Parameters.Count; i++) {
					if (i > first_parameter)
						sb.Write (", ");
					sb.Write (ToObjCParameterType (method.Parameters[i].ParameterType, descriptiveMethodName, exceptions, method));
					sb.Write (" ");
					sb.Write (GetParamName (method, i));
				}
				sb.WriteLine (");");

				sb.WriteLine (native_return_type);
				sb.Write (name);
				sb.Write (" (", method.Name);
				for (int i = first_parameter; i < method.Parameters.Count; i++) {
					if (i > first_parameter)
						sb.Write (", ");
					sb.Write (ToObjCParameterType (method.Parameters[i].ParameterType, descriptiveMethodName, exceptions, method));
					sb.Write (" ");
					sb.Write (GetParamName (method, i));
				}
				sb.WriteLine (")");
				sb.WriteLine ("{");
				sb.WriteLine ("@try {");
				if (!isVoid || is_stret)
					sb.Write ("return ");
				sb.Write ("((func_{0}) {1}) (", name, pinfo.EntryPoint);
				for (int i = first_parameter; i < method.Parameters.Count; i++) {
					if (i > first_parameter)
						sb.Write (", ");
					sb.Write (GetParamName (method, i));
				}
				sb.WriteLine (");");
				sb.WriteLine ("} @catch (NSException *exc) {");
				sb.WriteLine ("xamarin_process_nsexception (exc);");
				sb.WriteLine ("}");
				sb.WriteLine ("}");
				sb.WriteLine ();
			} else {
				// Console.WriteLine ("Signature already processed: {0} for {1}.{2}", signature.ToString (), method.DeclaringType.FullName, method.Name);
			}

			// find the module reference to __Internal
			ModuleReference mr = null;
			foreach (var mref in method.Module.ModuleReferences) {
				if (mref.Name == "__Internal") {
					mr = mref;
					break;
				}
			}
			if (mr == null)
				method.Module.ModuleReferences.Add (mr = new ModuleReference ("__Internal"));
			pinfo.Module = mr;
			pinfo.EntryPoint = wrapperName;
		}

		public void GenerateSingleAssembly (IEnumerable<AssemblyDefinition> assemblies, string header_path, string source_path, string assembly)
		{
			single_assembly = assembly;
			Generate (assemblies, header_path, source_path);
		}

		public void Generate (IEnumerable<AssemblyDefinition> assemblies, string header_path, string source_path)
		{
			this.input_assemblies = assemblies;

			foreach (var assembly in assemblies) {
				Driver.Log (3, "Generating static registrar for {0}", assembly.Name);
				RegisterAssembly (assembly);
			}

			Generate (header_path, source_path);
		}

		void Generate (string header_path, string source_path)
		{
			var sb = new AutoIndentStringBuilder ();
			header = new AutoIndentStringBuilder ();
			declarations = new AutoIndentStringBuilder ();
			methods = new AutoIndentStringBuilder ();
			interfaces = new AutoIndentStringBuilder ();

			header.WriteLine ("#pragma clang diagnostic ignored \"-Wdeprecated-declarations\"");
			header.WriteLine ("#pragma clang diagnostic ignored \"-Wtypedef-redefinition\""); // temporary hack until we can stop including glib.h
			header.WriteLine ("#pragma clang diagnostic ignored \"-Wobjc-designated-initializers\"");

			if (App.EnableDebug) {
				header.WriteLine ("#define DEBUG 1");
				methods.WriteLine ("#define DEBUG 1");
			}

			header.WriteLine ("#include <stdarg.h>");
			if (SupportsModernObjectiveC) {
				methods.WriteLine ("#include <xamarin/xamarin.h>");
			} else {
				header.WriteLine ("#include <xamarin/xamarin.h>");
			}
			header.WriteLine ("#include <objc/objc.h>");
			header.WriteLine ("#include <objc/runtime.h>");
			header.WriteLine ("#include <objc/message.h>");

			methods.WriteLine ($"#include \"{Path.GetFileName (header_path)}\"");
			methods.StringBuilder.AppendLine ("extern \"C\" {");

			if (App.Embeddinator)
				methods.WriteLine ("void xamarin_embeddinator_initialize ();");
			
			Specialize (sb);

			methods.StringBuilder.AppendLine ("} /* extern \"C\" */");

			FlushTrace ();

			header.AppendLine ();
			header.AppendLine (declarations);
			header.AppendLine (interfaces);
			Driver.WriteIfDifferent (header_path, header.ToString ());

			methods.WriteLine ();
			methods.AppendLine ();
			methods.AppendLine (sb);
			Driver.WriteIfDifferent (source_path, methods.ToString ());

			header.Dispose ();
			header = null;
			declarations.Dispose ();
			declarations = null;
			methods.Dispose ();
			methods = null;
			interfaces.Dispose ();
			interfaces = null;
			sb.Dispose ();
		}

		protected override bool SkipRegisterAssembly (AssemblyDefinition assembly)
		{
			if (assembly.HasCustomAttributes) {
				foreach (var ca in assembly.CustomAttributes) {
					var t = ca.AttributeType.Resolve ();
					while (t != null) {
						if (t.Is ("ObjCRuntime", "DelayedRegistrationAttribute"))
							return true;
						t = t.BaseType?.Resolve ();
					}
				}
			}

			return base.SkipRegisterAssembly (assembly);
		}
	}

	// Replicate a few attribute types here, with TypeDefinition instead of Type

	class ProtocolAttribute : Attribute {
		public TypeDefinition WrapperType { get; set; }
		public string Name { get; set; }
		public bool IsInformal { get; set; }
	}

	public sealed class ProtocolMemberAttribute : Attribute {
		public ProtocolMemberAttribute () {}

		public bool IsRequired { get; set; }
		public bool IsProperty { get; set; }
		public bool IsStatic { get; set; }
		public string Name { get; set; }
		public string Selector { get; set; }
		public TypeReference ReturnType { get; set; }
		public TypeReference[] ParameterType { get; set; }
		public bool[] ParameterByRef { get; set; }
		public bool IsVariadic { get; set; }

		public TypeReference PropertyType { get; set; }
		public string GetterSelector { get; set; }
		public string SetterSelector { get; set; }
		public ArgumentSemantic ArgumentSemantic { get; set; }
	}

	class CategoryAttribute : Attribute {
		public CategoryAttribute (TypeDefinition type)
		{
			Type = type;
		}

		public TypeDefinition Type { get; set; }
		public string Name { get; set; }
	}

	class RegisterAttribute : Attribute {
		public RegisterAttribute () {}
		public RegisterAttribute (string name) {
			this.Name = name;
		}

		public RegisterAttribute (string name, bool isWrapper) {
			this.Name = name;
			this.IsWrapper = isWrapper;
		}

		public string Name { get; set; }
		public bool IsWrapper { get; set; }
		public bool SkipRegistration { get; set; }
	}
}
