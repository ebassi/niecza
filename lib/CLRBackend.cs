// This is the new CLR backend.  The old one generated C# from Perl, which
// was slow and gave us the limitations of C#; this one aims to be faster.
// Also, by making the Perl code emit a portable format, it makes future
// portability work easier.

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Text;
using System.IO;

using Niecza;

namespace Niecza.CLRBackend {
    // The portable format is a subset of JSON, and is current read
    // into a matching internal form.
    sealed class JScalar {
        string text;
        double val;
        bool has_val;

        public JScalar(string txt) { text = txt; }
        public string str { get { return text; } }
        public double num {
            get {
                if (has_val) return val;
                val = double.Parse(text);
                has_val = true;
                return val;
            }
        }
        public override string ToString() { return text; }
    }

    class Reader {
        static char GetHexQuad(string s, int ix) {
            int acc = 0;
            for (int i = 0; i < 4; i++) {
                acc <<= 4;
                int ch = (int)s[ix+i];
                acc += (ch>=(int)'a'&&ch<=(int)'f') ? (ch + 10 - (int)'a') :
                       (ch>=(int)'A'&&ch<=(int)'F') ? (ch + 10 - (int)'A') :
                       (ch - (int)'0');
            }
            return (char)acc;
        }

        public static object Read(string input) {
            int ix = 0;
            List<List<object>> containers = new List<List<object>>();
            char i;
            StringBuilder sb;
            while (true) {
                i = input[ix];
                if (i == '\t' || i == ' ' || i == '\r' || i == '\n' ||
                        i == ',') {
                    ix++;
                    continue;
                }
                if (i == '[') {
                    containers.Add(new List<object>());
                    ix++;
                    continue;
                }
                if (i == ']') {
                    object[] r = containers[containers.Count - 1].ToArray();
                    containers.RemoveAt(containers.Count - 1);
                    if (containers.Count == 0) return r;
                    containers[containers.Count - 1].Add(r);
                    ix++;
                    continue;
                }
                if (i == 'n' && input.Length >= ix + 4 &&
                        input[ix+1] == 'u' && input[ix+2] == 'l' &&
                        input[ix+3] == 'l') {
                    containers[containers.Count - 1].Add(null);
                    ix += 4;
                    continue;
                }
                if (i == '"') {
                    sb = new StringBuilder();
                    ix++;
                    while (true) {
                        i = input[ix];
                        if (i == '\\') {
                            switch (input[ix+1]) {
                                case '/': i = '/'; break;
                                case '\\': i = '\\'; break;
                                case 't': i = '\t'; break;
                                case 'r': i = '\r'; break;
                                case 'n': i = '\n'; break;
                                case 'f': i = '\f'; break;
                                case 'b': i = '\b'; break;
                                case 'u': i = GetHexQuad(input, ix+2); ix += 4; break;
                            }
                            ix += 2;
                            sb.Append(i);
                        } else if (i == '"') {
                            break;
                        } else {
                            sb.Append(i);
                            ix++;
                        }
                    }
                    ix++;
                    containers[containers.Count - 1].Add(new JScalar(sb.ToString()));
                    continue;
                }
                sb = new StringBuilder();
                while (true) {
                    i = input[ix];
                    if (i == ',' || i == '\r' || i == '\t' || i == '\n' ||
                            i == ' ' || i == ']')
                        break;
                    sb.Append(i);
                    ix++;
                }
                containers[containers.Count - 1].Add(new JScalar(sb.ToString()));
            }
        }
    }

    // Because this is the *CLR* backend's Unit object, it's always
    // associated with either an Assembly or an AssemblyBuilder.
    class Unit {
        public readonly Xref mainline_ref;
        public readonly string name;
        public readonly object[] log;
        public readonly string setting;
        public readonly Xref bottom_ref;
        public readonly object[] xref;
        public readonly object[] tdeps;

        public Unit(object[] from) {
            mainline_ref = new Xref(from[0] as object[]);
            name = ((JScalar) from[1]).str;
            log = from[2] as object[];
            setting = from[3] == null ? null : ((JScalar) from[3]).str;
            bottom_ref = Xref.from(from[4] as object[]);
            xref = from[5] as object[];
            for (int i = 0; i < xref.Length; i++) {
                if (xref[i] == null) continue;
                object[] xr = (object[]) xref[i];
                if (xr.Length > 6) {
                    xref[i] = new StaticSub(this, xr);
                } else {
                    xref[i] = Package.From(xr);
                }
            }
            tdeps = from[6] as object[];
        }

        public void VisitSubsPostorder(Action<int,StaticSub> cb) {
            DoVisitSubsPostorder(mainline_ref.index, cb);
        }

        public void VisitPackages(Action<int,Package> cb) {
            for (int i = 0; i < xref.Length; i++) {
                Package p = xref[i] as Package;
                if (p != null)
                    cb(i, p);
            }
        }

        private void DoVisitSubsPostorder(int ix, Action<int,StaticSub> cb) {
            StaticSub s = xref[ix] as StaticSub;
            foreach (int z in s.zyg)
                DoVisitSubsPostorder(z, cb);
            cb(ix,s);
        }

        public static string SharedName(char type, int ix, string name) {
            StringBuilder sb = new StringBuilder();
            sb.Append(type);
            sb.Append(ix);
            sb.Append('_');
            foreach (char c in name) {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z')
                        || (c >= 'A' && c <= 'Z')) {
                    sb.Append(c);
                } else if (c == '_') {
                    sb.Append("__");
                } else if (c <= (char)255) {
                    sb.AppendFormat("_{0:X2}", (int)c);
                } else {
                    sb.AppendFormat("_U{0:X4}", (int)c);
                }
            }
            return sb.ToString();
        }

        public void BindFields(Func<string,Type,FieldInfo> binder) {
            VisitSubsPostorder(delegate(int ix, StaticSub sub) {
                sub.BindFields(ix, binder);
            });
            VisitPackages(delegate(int ix, Package pkg) {
                pkg.BindFields(ix, binder);
            });
        }
    }

    class Xref {
        public readonly string unit;
        public readonly int index;
        public readonly string name;

        public static Xref from(object[] x) {
            return (x == null) ? null : new Xref(x);
        }
        public Xref(object[] from) : this(from, 0) {}
        public Xref(object[] from, int ofs) {
            unit  = ((JScalar)from[ofs+0]).str;
            index = (int)((JScalar)from[ofs+1]).num;
            name  = ((JScalar)from[ofs+2]).str;
        }
        public object Resolve() { return CLRBackend.Resolve(this); }
    }

    class Package {
        public readonly string name;
        public readonly string type;

        public Package(object[] p) {
            type = ((JScalar)p[0]).str;
            name = ((JScalar)p[1]).str;
        }

        public virtual void BindFields(int ix,
                Func<string,Type,FieldInfo> binder) { }

        public static Package From(object[] p) {
            string type = ((JScalar)p[0]).str;
            if (type == "parametricrole")
                return new ParametricRole(p);
            else if (type == "role")
                return new Role(p);
            else if (type == "class")
                return new Class(p);
            else if (type == "grammar")
                return new Grammar(p);
            else if (type == "module")
                return new Module(p);
            else if (type == "package")
                return new Package(p);
            else
                throw new Exception("unknown package type " + p);
        }
    }

    class Module: Package {
        public Module(object[] p) : base(p) { }
    }

    class ModuleWithTypeObject: Module {
        public FieldInfo typeObject;
        public FieldInfo typeVar;
        public FieldInfo metaObject;

        public override void BindFields(int ix,
                Func<string,Type,FieldInfo> binder) {
            typeObject = binder(Unit.SharedName('T', ix, name), Tokens.IP6);
            typeVar    = binder(Unit.SharedName('V', ix, name), Tokens.Variable);
            metaObject = binder(Unit.SharedName('M', ix, name), Tokens.DynMetaObject);
        }

        public ModuleWithTypeObject(object[] p) : base(p) { }
    }

    class Class: ModuleWithTypeObject {
        public readonly object attributes;
        public readonly object methods;
        public readonly object superclasses;
        public readonly object linearized_mro;
        public Class(object[] p) : base(p) {
            attributes = p[2];
            methods = p[3];
            superclasses = p[4];
            linearized_mro = p[5];
        }
    }

    class Grammar: Class {
        public Grammar(object[] p) : base(p) { }
    }

    class Role: ModuleWithTypeObject {
        public readonly object attributes;
        public readonly object methods;
        public readonly object superclasses;
        public Role(object[] p) : base(p) {
            attributes = p[2];
            methods = p[3];
            superclasses = p[4];
        }
    }

    class ParametricRole: ModuleWithTypeObject {
        public readonly object attributes;
        public readonly object methods;
        public readonly object superclasses;
        public ParametricRole(object[] p) : base(p) {
            attributes = p[2];
            methods = p[3];
            superclasses = p[4];
        }
    }

    class StaticSub {
        public const int RUN_ONCE = 1;
        public const int SPAD_EXISTS = 2;
        public const int GATHER_HACK = 4;
        public const int STRONG_USED = 8;
        public const int RETURNABLE = 16;
        public const int AUGMENTING = 32;
        public readonly string name;
        public readonly Unit unit;
        public readonly Xref outer;
        public readonly int flags;
        public readonly int[] zyg;
        public readonly object parametric_role_hack;
        public readonly object augment_hack;
        public readonly int is_phaser;
        public readonly Xref body_of;
        public readonly Xref in_class;
        public readonly string[] cur_pkg;
        public readonly List<KeyValuePair<string,Lexical>> lexicals;
        public readonly Dictionary<string,Lexical> l_lexicals;
        public readonly object body;

        public FieldInfo protosub;
        public FieldInfo subinfo;
        public FieldInfo protopad;
        public int nlexn;

        public StaticSub(Unit unit, object[] s) {
            this.unit = unit;
            name = ((JScalar)s[0]).str;
            outer = Xref.from(s[1] as object[]);
            flags = (int) ((JScalar)s[2]).num;
            object[] r_zyg = s[3] as object[];
            parametric_role_hack = s[4];
            augment_hack = s[5];
            is_phaser = s[6] == null ? -1 : (int) ((JScalar) s[6]).num;
            body_of = Xref.from(s[7] as object[]);
            in_class = Xref.from(s[8] as object[]);
            object[] r_cur_pkg = s[9] as object[];

            zyg = new int[ r_zyg.Length ];
            for (int i = 0; i < r_zyg.Length; i++)
                zyg[i] = (int) ((JScalar) r_zyg[i]).num;

            cur_pkg = new string[ r_cur_pkg.Length ];
            for (int i = 0; i < r_cur_pkg.Length; i++)
                cur_pkg[i] = ((JScalar) r_cur_pkg[i]).str;

            object[] r_lexicals = s[14] as object[];
            body = s[15];
            lexicals = new List<KeyValuePair<string,Lexical>>();
            l_lexicals = new Dictionary<string,Lexical>();
            for (int i = 0; i < r_lexicals.Length; i++) {
                object[] bl = r_lexicals[i] as object[];
                string lname = ((JScalar)bl[0]).str;
                string type = ((JScalar)bl[1]).str;
                Lexical obj = null;

                if (type == "simple") {
                    obj = new LexSimple(bl);
                } else if (type == "common") {
                    obj = new LexCommon(bl);
                } else if (type == "sub") {
                    obj = new LexSub(bl);
                } else if (type == "alias") {
                    obj = new LexAlias(bl);
                } else if (type == "stash") {
                    obj = new LexStash(bl);
                } else {
                    throw new Exception("unknown lex type " + type);
                }

                lexicals.Add(new KeyValuePair<string,Lexical>(lname, obj));
                l_lexicals[lname] = obj;
            }
        }

        public bool IsCore() {
            string s = unit.name;
            return (s == "SAFE" || s == "CORE");
        }

        public void BindFields(int ix, Func<string,Type,FieldInfo> binder) {
            subinfo  = binder(Unit.SharedName('I', ix, name), Tokens.SubInfo);
            if ((flags & SPAD_EXISTS) != 0)
                protopad = binder(Unit.SharedName('P', ix, name), Tokens.Frame);
            if (outer == null || (((StaticSub)outer.Resolve()).flags
                        & SPAD_EXISTS) != 0)
                protosub = binder(Unit.SharedName('S', ix, name), Tokens.IP6);

            nlexn = 0;
            for (int i = 0; i < lexicals.Count; i++)
                if (lexicals[i].Value != null) // XXX
                    lexicals[i].Value.BindFields(ix, i, this,
                            lexicals[i].Key, binder);
        }
    }

    abstract class Lexical {
        public virtual void BindFields(int six, int lix, StaticSub sub,
                string name, Func<string,Type,FieldInfo> binder) { }
        public virtual ClrOp SetCode(int up, ClrOp head) {
            throw new Exception("Lexicals of type " + this + " cannot be bound");
        }
        public abstract ClrOp GetCode(int up);
        protected static bool IsDynamicName(string name) {
            if (name == "$_") return true;
            if (name.Length < 2) return false;
            if (name[0] == '*' || name[0] == '?') return true;
            if (name[1] == '*' || name[1] == '?') return true;
            return false;
        }
    }

    class LexSimple : Lexical {
        public const int NOINIT = 4;
        public const int LIST = 2;
        public const int HASH = 1;
        public readonly int flags;

        public int index;
        public FieldInfo stg;

        public LexSimple(object[] l) {
            flags = (int)((JScalar)l[2]).num;
        }

        public override ClrOp GetCode(int up) {
            return (index >= 0) ? (ClrOp)new ClrPadGet(up, index)
                                : new ClrGetSField(stg);
        }

        public override ClrOp SetCode(int up, ClrOp to) {
            return (index >= 0) ? (ClrOp)new ClrPadSet(up, index, to)
                                : new ClrSetSField(stg, to);
        }

        public override void BindFields(int six, int lix, StaticSub sub,
                string name, Func<string,Type,FieldInfo> binder) {
            if (IsDynamicName(name) || (sub.flags & StaticSub.RUN_ONCE) == 0) {
                index = sub.nlexn++;
            } else {
                index = -1;
                stg = binder(Unit.SharedName('L', six, name), Tokens.Variable);
                sub.nlexn++;
            }
        }
    }

    class LexCommon : Lexical {
        public readonly string[] path;
        public FieldInfo stg;
        public LexCommon(object[] l) {
            path = new string[l.Length - 2];
            for (int i = 2; i < l.Length; i++)
                path[i-2] = ((JScalar)l[i]).str;
        }
        public override ClrOp GetCode(int up) {
            return new ClrGetField(Tokens.BValue_v,
                    new ClrGetSField(stg));
        }
        public override ClrOp SetCode(int up, ClrOp to) {
            return new ClrSetField(Tokens.BValue_v,
                    new ClrGetSField(stg), to);
        }
        public override void BindFields(int six, int lix, StaticSub sub,
                string name, Func<string,Type,FieldInfo> binder) {
            stg = binder(Unit.SharedName('B', six, name), Tokens.BValue);
        }
    }

    class LexSub : Lexical {
        public readonly Xref def;
        public LexSub(object[] l) {
            def = new Xref(l, 2);
        }

        public int index;
        public FieldInfo stg;

        public override ClrOp GetCode(int up) {
            return (index >= 0) ? (ClrOp)new ClrPadGet(up, index)
                                : new ClrGetSField(stg);
        }

        public override void BindFields(int six, int lix, StaticSub sub,
                string name, Func<string,Type,FieldInfo> binder) {
            if (IsDynamicName(name) || (sub.flags & StaticSub.RUN_ONCE) == 0) {
                index = sub.nlexn++;
            } else {
                index = -1;
                stg = binder(Unit.SharedName('L', six, name), Tokens.Variable);
                sub.nlexn++;
            }
        }
    }

    class LexAlias : Lexical {
        public readonly string to;
        public LexAlias(object[] l) {
            to = ((JScalar)l[2]).str;
        }
        public override ClrOp GetCode(int up) { throw new NotImplementedException(); }
    }

    class LexStash : Lexical {
        public readonly string[] path;
        public override ClrOp GetCode(int up) {
            throw new NotImplementedException();
        }
        public LexStash(object[] l) {
            path = new string[l.Length - 2];
            for (int i = 0; i < path.Length; i++)
                path[i] = ((JScalar)l[i+2]).str;
        }
    }

    // Extra info needed beyond what ILGenerator alone provides.  Note
    // that switch generation is done in another pass.
    class CgContext {
        public ILGenerator il;
        public int next_case;
        public Label[] cases;
        public int num_cases;
        public Dictionary<string,int> named_cases
            = new Dictionary<string,int>();
        public Dictionary<string,Label> named_labels
            = new Dictionary<string,Label>();
        public string[] let_names = new string[0];
        public Type[] let_types = new Type[0];
        public LocalBuilder ospill;

        public void make_ospill() {
            if (ospill == null)
                ospill = il.DeclareLocal(Tokens.Variable);
        }

        // logic stolen from mcs
        public void EmitInt(int i) {
            switch (i) {
                case -1: il.Emit(OpCodes.Ldc_I4_M1); break;
                case 0: il.Emit(OpCodes.Ldc_I4_0); break;
                case 1: il.Emit(OpCodes.Ldc_I4_1); break;
                case 2: il.Emit(OpCodes.Ldc_I4_2); break;
                case 3: il.Emit(OpCodes.Ldc_I4_3); break;
                case 4: il.Emit(OpCodes.Ldc_I4_4); break;
                case 5: il.Emit(OpCodes.Ldc_I4_5); break;
                case 6: il.Emit(OpCodes.Ldc_I4_6); break;
                case 7: il.Emit(OpCodes.Ldc_I4_7); break;
                case 8: il.Emit(OpCodes.Ldc_I4_8); break;
                default:
                    if (i >= -128 && i < 127) {
                        il.Emit(OpCodes.Ldc_I4_S, (sbyte) i);
                    } else {
                        il.Emit(OpCodes.Ldc_I4, i);
                    }
                    break;
            }
        }

        public void EmitPreSetlex(int ix) {
            if (ix >= (Tokens.NumInt32 + Tokens.NumInline)) {
                il.Emit(OpCodes.Ldfld, Tokens.Frame_lexn);
                EmitInt(ix - (Tokens.NumInt32 + Tokens.NumInline));
            }
        }

        public void EmitSetlex(int ix, Type t) {
            if (ix >= Tokens.NumInt32 && t.IsValueType)
                il.Emit(OpCodes.Box, t);

            if (ix >= (Tokens.NumInt32 + Tokens.NumInline)) {
                il.Emit(OpCodes.Stelem_Ref);
            } else if (ix >= Tokens.NumInt32) {
                il.Emit(OpCodes.Stfld,
                        Tokens.Frame_lexobj[ix - Tokens.NumInt32]);
            } else {
                il.Emit(OpCodes.Stfld, Tokens.Frame_lexi32[ix]);
            }
        }

        public void EmitGetlex(int ix, Type t) {
            if (ix >= (Tokens.NumInt32 + Tokens.NumInline)) {
                il.Emit(OpCodes.Ldfld, Tokens.Frame_lexn);
                EmitInt(ix - (Tokens.NumInt32 + Tokens.NumInline));
                il.Emit(OpCodes.Ldelem_Ref);
            } else if (ix >= Tokens.NumInt32) {
                il.Emit(OpCodes.Ldfld,
                        Tokens.Frame_lexobj[ix - Tokens.NumInt32]);
            } else {
                il.Emit(OpCodes.Ldfld, Tokens.Frame_lexi32[ix]);
            }

            if (ix >= Tokens.NumInt32) {
                il.Emit(OpCodes.Unbox_Any, t);
            }
        }
    }

    sealed class Tokens {
        public static readonly Type Void = typeof(void);
        public static readonly Type String = typeof(string);
        public static readonly Type Boolean = typeof(bool);
        public static readonly Type Int32 = typeof(int);
        public static readonly Type Double = typeof(double);
        public static readonly Type Frame = typeof(Frame);
        public static readonly Type SubInfo = typeof(SubInfo);
        public static readonly Type IP6 = typeof(IP6);
        public static readonly Type Variable = typeof(Variable);
        public static readonly Type BValue = typeof(BValue);
        public static readonly Type DynMetaObject = typeof(DynMetaObject);

        public static readonly ConstructorInfo DynBlockDelegate_ctor =
            typeof(DynBlockDelegate).GetConstructor(new Type[] {
                    typeof(object), typeof(IntPtr) });

        public static readonly MethodInfo Variable_Fetch =
            Variable.GetMethod("Fetch");
        public static readonly MethodInfo Kernel_Die =
            typeof(Kernel).GetMethod("Die");
        public static readonly MethodInfo Kernel_RunLoop =
            typeof(Kernel).GetMethod("RunLoop");
        public static readonly MethodInfo Console_WriteLine =
            typeof(Console).GetMethod("WriteLine", new Type[] { typeof(string) });
        public static readonly MethodInfo Console_Write =
            typeof(Console).GetMethod("Write", new Type[] { typeof(string) });
        public static readonly MethodInfo Environment_Exit =
            typeof(Console).GetMethod("Exit");
        public static readonly MethodInfo Object_ToString =
            typeof(object).GetMethod("ToString", new Type[0]);

        public static readonly FieldInfo IP6_mo =
            IP6.GetField("mo");
        public static readonly FieldInfo BValue_v =
            BValue.GetField("v");
        public static readonly FieldInfo Frame_ip =
            typeof(Frame).GetField("ip");
        public static readonly FieldInfo Frame_caller =
            typeof(Frame).GetField("caller");
        public static readonly FieldInfo Frame_outer =
            typeof(Frame).GetField("outer");
        public static readonly FieldInfo Frame_resultSlot =
            typeof(Frame).GetField("resultSlot");
        public static readonly FieldInfo Frame_lexn =
            typeof(Frame).GetField("lexn");
        public static readonly FieldInfo[] Frame_lexi32 = new FieldInfo[] {
            typeof(Frame).GetField("lexi0"),
            typeof(Frame).GetField("lexi1")
        };
        public static readonly FieldInfo[] Frame_lexobj = new FieldInfo[] {
            typeof(Frame).GetField("lex0"),
            typeof(Frame).GetField("lex1"),
            typeof(Frame).GetField("lex2"),
            typeof(Frame).GetField("lex3"),
            typeof(Frame).GetField("lex4"),
            typeof(Frame).GetField("lex5"),
            typeof(Frame).GetField("lex6"),
            typeof(Frame).GetField("lex7"),
            typeof(Frame).GetField("lex8"),
            typeof(Frame).GetField("lex9")
        };

        public const int NumInt32 = 2;
        public const int NumInline = 10;
    }

    // This are expressional CLR operators.  This is lower level than the
    // CPS stuff; if HasCases is true, Returns must be void.  Thus,
    // there is no need to handle argument spills.
    abstract class ClrOp {
        public bool HasCases;
        public bool Constant; // if this returns a value, can it be reordered?
        public Type Returns;
        public abstract void CodeGen(CgContext cx);
        public virtual void ListCases(CgContext cx) { }

        protected static void TypeCheck(Type sub, Type super, string msg) {
            if (!super.IsAssignableFrom(sub))
                throw new Exception(msg + " " + sub + " not subtype of " + super);
        }
    }

    class ClrMethodCall : ClrOp {
        public readonly MethodInfo Method;
        public readonly ClrOp[] Zyg;

        public override void CodeGen(CgContext cx) {
            if (HasCases) {
                cx.il.Emit(OpCodes.Ldarg_0);
                cx.EmitInt(cx.next_case);
                cx.il.Emit(OpCodes.Stfld, Tokens.Frame_ip);
            }
            int i = 0;
            foreach (ClrOp o in Zyg) {
                if (HasCases && i == (Method.IsStatic ? 0 : 1)) {
                    // this needs to come AFTER the invocant
                    cx.il.Emit(OpCodes.Ldarg_0);
                }
                o.CodeGen(cx);

                // XXX this doesn't work quite right if the method is
                // defined on the value type itself
                if (i == 0 && o.Returns.IsValueType && !Method.IsStatic)
                    cx.il.Emit(OpCodes.Box, o.Returns);
            }
            cx.il.Emit((Method.IsStatic ? OpCodes.Call : OpCodes.Callvirt),
                    Method); // XXX C#
            if (HasCases) {
                cx.il.Emit(OpCodes.Ret);
                cx.il.MarkLabel(cx.cases[cx.next_case++]);
            }
        }

        public override void ListCases(CgContext cx) {
            // it is not legal for any of out children to have cases to list
            if (HasCases)
                cx.num_cases++;
        }

        public ClrMethodCall(bool cps, MethodInfo mi, ClrOp[] zyg) {
            Method = mi;
            Zyg = zyg;
            Returns = cps ? Tokens.Void : mi.ReturnType;
            HasCases = cps;

            List<Type> ts = new List<Type>();

            if (!mi.IsStatic)
                ts.Add(mi.DeclaringType);

            bool skip = cps;
            foreach (ParameterInfo pi in mi.GetParameters()) {
                if (skip) { skip = false; continue; }
                ts.Add(pi.ParameterType);
            }

            if (zyg.Length != ts.Count)
                throw new Exception("argument list length mismatch");

            for (int i = 0; i < ts.Count; i++) {
                TypeCheck(zyg[i].Returns, ts[i], "arg");
            }
        }
    }

    class ClrContexty : ClrOp {
        public readonly ClrOp[] zyg;
        public readonly MethodInfo inv;
        public readonly FieldInfo thing;

        public override void CodeGen(CgContext cx) {
            zyg[0].CodeGen(cx);
            cx.make_ospill();
            cx.il.Emit(OpCodes.Stloc, cx.ospill);
            cx.il.Emit(OpCodes.Ldloc, cx.ospill);
            cx.il.Emit(OpCodes.Callvirt, Tokens.Variable_Fetch);
            cx.il.Emit(OpCodes.Ldfld, Tokens.IP6_mo);
            cx.il.Emit(OpCodes.Ldfld, thing);
            cx.il.Emit(OpCodes.Ldfld, cx.ospill);
            for (int i = 1; i < zyg.Length; i++)
                zyg[i].CodeGen(cx);
            cx.il.Emit(OpCodes.Callvirt, inv);
        }

        public ClrContexty(FieldInfo thing, MethodInfo inv, ClrOp[] zyg) {
            this.thing = thing;
            this.inv = inv;
            this.zyg = zyg;
            Returns = inv.ReturnType;
        }
    }

    class ClrOperator : ClrOp {
        public readonly OpCode op;
        public readonly ClrOp[] zyg;

        public override void CodeGen(CgContext cx) {
            foreach (ClrOp c in zyg)
                c.CodeGen(cx);
            cx.il.Emit(op);
        }

        public ClrOperator(Type ret, OpCode op, ClrOp[] zyg) {
            Returns = ret;
            this.op = op;
            this.zyg = zyg;
        }
    }

    class ClrGetField : ClrOp {
        public readonly FieldInfo f;
        public readonly ClrOp zyg;

        public override void CodeGen(CgContext cx) {
            zyg.CodeGen(cx);
            cx.il.Emit(OpCodes.Ldfld, f);
        }

        public ClrGetField(FieldInfo f, ClrOp zyg) {
            Returns = f.FieldType;
            this.f = f;
            this.zyg = zyg;
        }
    }

    class ClrSetField : ClrOp {
        public readonly FieldInfo f;
        public readonly ClrOp zyg1;
        public readonly ClrOp zyg2;

        public override void CodeGen(CgContext cx) {
            zyg1.CodeGen(cx);
            zyg2.CodeGen(cx);
            cx.il.Emit(OpCodes.Stfld, f);
        }

        public ClrSetField(FieldInfo f, ClrOp zyg1, ClrOp zyg2) {
            Returns = Tokens.Void;
            this.f = f;
            this.zyg1 = zyg1;
            this.zyg2 = zyg2;
        }
    }

    class ClrGetSField : ClrOp {
        public readonly FieldInfo f;

        public override void CodeGen(CgContext cx) {
            cx.il.Emit(OpCodes.Ldsfld, f);
        }

        public ClrGetSField(FieldInfo f) {
            Returns = f.FieldType;
            this.f = f;
        }
    }

    class ClrSetSField : ClrOp {
        public readonly FieldInfo f;
        public readonly ClrOp zyg;

        public override void CodeGen(CgContext cx) {
            zyg.CodeGen(cx);
            cx.il.Emit(OpCodes.Stsfld, f);
        }

        public ClrSetSField(FieldInfo f, ClrOp zyg) {
            Returns = Tokens.Void;
            this.f = f;
            this.zyg = zyg;
        }
    }

    class ClrPadGet : ClrOp {
        public readonly int up;
        public readonly int index;

        public override void CodeGen(CgContext cx) {
            cx.il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < up; i++)
                cx.il.Emit(OpCodes.Ldfld, Tokens.Frame_outer);
            cx.EmitGetlex(index, Tokens.Variable);
        }

        public ClrPadGet(int up, int index) {
            Returns = Tokens.Variable;
            this.up = up;
            this.index = index;
        }
    }

    class ClrPadSet : ClrOp {
        public readonly int up;
        public readonly int index;
        public readonly ClrOp zyg;

        public override void CodeGen(CgContext cx) {
            cx.il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < up; i++)
                cx.il.Emit(OpCodes.Ldfld, Tokens.Frame_outer);
            cx.EmitPreSetlex(index);
            zyg.CodeGen(cx);
            cx.EmitSetlex(index, Tokens.Variable);
        }

        public ClrPadSet(int up, int index, ClrOp zyg) {
            Returns = Tokens.Void;
            this.zyg = zyg;
            this.up = up;
            this.index = index;
        }
    }

    class ClrNoop : ClrOp {
        private ClrNoop() {
            Returns = Tokens.Void;
            HasCases = false;
        }
        public override void CodeGen(CgContext cx) { }
        public static ClrNoop Instance = new ClrNoop();
    }

    class ClrPushLet : ClrOp {
        string Name;
        // Initial must not have a net let-stack effect (how to enforce?)
        ClrOp Initial;
        public ClrPushLet(string name, ClrOp initial) {
            Initial = initial;
            Name = name;
            Returns = Tokens.Void;
        }
        public override void CodeGen(CgContext cx) {
            // indexes 0-1 can only be used by ints
            int ix = (Initial.Returns == typeof(int)) ? 0 : Tokens.NumInt32;
            while (ix < cx.let_types.Length && cx.let_types[ix] != null)
                ix++;

            cx.il.Emit(OpCodes.Ldarg_0);
            cx.EmitPreSetlex(ix);

            // Initial must not have a net effect on cx.let_types
            Initial.CodeGen(cx);

            // let_types.Length tracks the highest index used.
            if (ix >= cx.let_types.Length) {
                Array.Resize(ref cx.let_types, ix+1);
                Array.Resize(ref cx.let_names, ix+1);
            }

            cx.let_types[ix] = Initial.Returns;
            cx.let_names[ix] = Name;

            cx.EmitSetlex(ix, Initial.Returns);
        }
    }

    class ClrPokeLet : ClrOp {
        string Name;
        ClrOp Value;
        public ClrPokeLet(string name, ClrOp value) {
            Value = value;
            Name = name;
            Returns = Tokens.Void;
        }
        public override void CodeGen(CgContext cx) {
            int ix = cx.let_names.Length - 1;
            while (ix >= 0 && cx.let_names[ix] != Name)
                ix--;

            if (ix == cx.let_names.Length)
                throw new Exception("let " + Name + " not found");

            cx.il.Emit(OpCodes.Ldarg_0);
            cx.EmitPreSetlex(ix);

            // Initial must not have a net effect on cx.let_types
            Value.CodeGen(cx);

            cx.EmitSetlex(ix, Value.Returns);
        }
    }

    class ClrPeekLet : ClrOp {
        string Name;
        public ClrPeekLet(string name, Type letType) {
            Name = name;
            Returns = letType;
        }
        public override void CodeGen(CgContext cx) {
            int ix = cx.let_names.Length - 1;
            while (ix >= 0 && cx.let_names[ix] != Name)
                ix--;

            if (ix == cx.let_names.Length)
                throw new Exception("let " + Name + " not found");

            cx.il.Emit(OpCodes.Ldarg_0);
            cx.EmitGetlex(ix, Returns);
        }
    }

    class ClrDropLet : ClrOp {
        string Name;
        ClrOp Inner;
        public ClrDropLet(string name, ClrOp inner) {
            Name = name;
            Inner = inner;
            Returns = inner.Returns;
            HasCases = inner.HasCases;
        }
        public override void ListCases(CgContext cx) {
            Inner.ListCases(cx);
        }
        public override void CodeGen(CgContext cx) {
            Inner.CodeGen(cx);

            int ix = cx.let_names.Length - 1;
            while (ix >= 0 && cx.let_names[ix] != Name)
                ix--;

            if (ix == cx.let_names.Length)
                throw new Exception("let " + Name + " not found");

            cx.let_names[ix] = null;
            cx.let_types[ix] = null;
            // XXX We probably should null reference-valued lets here
        }
    }

    // TODO Investigate DLR-style labels with arguments
    class ClrLabel : ClrOp {
        string name;
        bool case_too;
        public ClrLabel(string name, bool case_too) {
            this.name = name;
            this.case_too = case_too;
            Returns = Tokens.Void;
            HasCases = true;
        }
        public override void ListCases(CgContext cx) {
            cx.named_labels[name] = cx.il.DefineLabel();
            if (case_too)
                cx.named_cases[name] = cx.num_cases++;
        }
        public override void CodeGen(CgContext cx) {
            cx.il.MarkLabel(cx.named_labels[name]);
            if (case_too)
                cx.il.MarkLabel(cx.cases[cx.named_cases[name]]);
        }
    }

    class ClrGoto : ClrOp {
        string name;
        bool iffalse;
        ClrOp inner;
        public ClrGoto(string name, bool iffalse, ClrOp inner) {
            this.name = name;
            this.iffalse = iffalse;
            this.inner = inner;
            Returns = Tokens.Void;
        }
        public override void CodeGen(CgContext cx) {
            // TODO: peephole optimize ceq/brtrue and similar forms
            Label l = cx.named_labels[name];
            if (inner != null) {
                inner.CodeGen(cx);
                cx.il.Emit(iffalse ? OpCodes.Brfalse : OpCodes.Brtrue, l);
            } else {
                cx.il.Emit(OpCodes.Br, l);
            }
        }
    }

    class ClrCpsReturn : ClrOp {
        ClrOp child;
        public ClrCpsReturn(ClrOp child) {
            this.child = child;
        }
        public override void CodeGen(CgContext cx) {
            if (child != null) {
                cx.il.Emit(OpCodes.Ldarg_0);
                cx.il.Emit(OpCodes.Ldfld, Tokens.Frame_caller);
                child.CodeGen(cx);
                cx.il.Emit(OpCodes.Stfld, Tokens.Frame_resultSlot);
            }
            cx.il.Emit(OpCodes.Ldarg_0);
            cx.il.Emit(OpCodes.Ldfld, Tokens.Frame_caller);
            cx.il.Emit(OpCodes.Ret);
        }
    }

    class ClrStringLiteral : ClrOp {
        string data;
        public ClrStringLiteral(string data) {
            this.data = data;
            Returns = Tokens.String;
            Constant = true;
        }
        public override void CodeGen(CgContext cx) {
            cx.il.Emit(OpCodes.Ldstr, data);
        }
    }

    // Because the CLR has no evaluation stack types narrower than int32, this
    // node does duty both for int and bool.  When sized types are added, it
    // will also handle int8, int16, and unsigned versions thereof.
    class ClrIntLiteral : ClrOp {
        int data;
        public ClrIntLiteral(Type ty, int data) {
            this.data = data;
            Returns = ty;
            Constant = true;
        }
        public override void CodeGen(CgContext cx) {
            cx.EmitInt(data);
        }
    }

    class ClrNumLiteral : ClrOp {
        double data;
        public ClrNumLiteral(double data) {
            this.data = data;
            Returns = Tokens.Double;
            Constant = true;
        }
        public override void CodeGen(CgContext cx) {
            cx.il.Emit(OpCodes.Ldc_R8, data);
        }
    }

    // CpsOps are rather higher level, and can support operations that
    // both return to the trampoline and return a value.
    class CpsOp {
        // each statement MUST return void
        public ClrOp[] stmts;
        // the head MUST NOT have cases
        public ClrOp head;

        public CpsOp(ClrOp head) : this(new ClrOp[0], head) { }
        public CpsOp(ClrOp[] stmts, ClrOp head) {
            if (head.HasCases)
                throw new Exception("head must not have cases");
            foreach (ClrOp s in stmts)
                if (s.Returns != Tokens.Void)
                    throw new Exception("stmts must return void");
            this.head = head;
            this.stmts = stmts;
        }

        // XXX only needs to be unique per sub
        [ThreadStatic] private static int nextunique = 0;
        // this particular use of a delegate feels wrong
        private static CpsOp Primitive(CpsOp[] zyg, Func<ClrOp[],ClrOp> raw) {
            List<ClrOp> stmts = new List<ClrOp>();
            List<ClrOp> args = new List<ClrOp>();
            List<string> pop = new List<string>();

            for (int i = 0; i < zyg.Length; i++) {
                foreach (ClrOp s in zyg[i].stmts)
                    stmts.Add(s);

                bool more_stmts = false;
                for (int j = i + 1; j < zyg.Length; j++)
                    if (zyg[j].stmts.Length != 0)
                        more_stmts = true;

                if (!more_stmts || zyg[i].head.Constant) {
                    args.Add(zyg[i].head);
                } else {
                    string ln = "!spill" + (nextunique++);
                    args.Add(new ClrPeekLet(ln, zyg[i].head.Returns));
                    stmts.Add(new ClrPushLet(ln, zyg[i].head));
                    pop.Add(ln);
                }
            }

            ClrOp head = raw(args.ToArray());
            for (int i = pop.Count - 1; i >= 0; i--)
                head = new ClrDropLet(pop[i], head);
            return new CpsOp(stmts.ToArray(), head);
        }

        public static CpsOp Sequence(CpsOp[] terms) {
            if (terms.Length == 0) return new CpsOp(ClrNoop.Instance);

            List<ClrOp> stmts = new List<ClrOp>();
            for (int i = 0; i < terms.Length - 1; i++) {
                if (terms[i].head.Returns != Tokens.Void)
                    throw new Exception("Non-void expression used in nonfinal sequence position");
                foreach (ClrOp s in terms[i].stmts)
                    stmts.Add(s);
                stmts.Add(terms[i].head);
            }

            foreach (ClrOp s in terms[terms.Length - 1].stmts)
                stmts.Add(s);
            return new CpsOp(stmts.ToArray(), terms[terms.Length - 1].head);
        }

        public static CpsOp MethodCall(bool cps, MethodInfo tk, CpsOp[] zyg) {
            return Primitive(zyg, delegate (ClrOp[] heads) {
                return new ClrMethodCall(cps, tk, heads);
            });
        }

        public static CpsOp CpsReturn(CpsOp[] zyg) {
            return Primitive(zyg, delegate (ClrOp[] heads) {
                return new ClrCpsReturn(heads.Length > 0 ? heads[0] : null);
            });
        }

        public static CpsOp Goto(string label, bool iffalse, CpsOp[] zyg) {
            return Primitive(zyg, delegate (ClrOp[] heads) {
                return new ClrGoto(label, iffalse,
                    heads.Length > 0 ? heads[0] : null);
            });
        }

        public static CpsOp StringLiteral(string s) {
            return new CpsOp(new ClrStringLiteral(s));
        }

        public static CpsOp IntLiteral(int x) {
            return new CpsOp(new ClrIntLiteral(Tokens.Int32, x));
        }

        public static CpsOp BoolLiteral(int x) {
            return new CpsOp(new ClrIntLiteral(Tokens.Boolean, x));
        }

        public static CpsOp Label(string name, bool case_too) {
            return new CpsOp(new ClrOp[] { new ClrLabel(name, case_too) },
                    ClrNoop.Instance);
        }

        // A previous niecza backend had this stuff tie into the
        // lifter, such that var->obj predictably happened as the
        // beginning.  But TimToady says that's not needed.
        public static CpsOp Contexty(FieldInfo thing, MethodInfo inv,
                CpsOp[] zyg) {
            return Primitive(zyg, delegate(ClrOp[] heads) {
                return new ClrContexty(thing, inv, heads);
            });
        }

        public static CpsOp Operator(Type rt, OpCode op, CpsOp[] zyg) {
            return Primitive(zyg, delegate(ClrOp[] heads) {
                return new ClrOperator(rt, op, heads);
            });
        }

        public static CpsOp PokeLet(string name, CpsOp[] zyg) {
            return Primitive(zyg, delegate(ClrOp[] heads) {
                return new ClrPokeLet(name, heads[0]);
            });
        }

        public static CpsOp PeekLet(string name, Type rt) {
            return new CpsOp(new ClrPeekLet(name, rt));
        }

        public static CpsOp Let(string name, CpsOp head, CpsOp tail) {
            List<ClrOp> stmts = new List<ClrOp>();
            foreach (ClrOp c in head.stmts)
                stmts.Add(c);
            stmts.Add(new ClrPushLet(name, head.head));
            foreach (ClrOp c in tail.stmts)
                stmts.Add(c);
            return new CpsOp(stmts.ToArray(), new ClrDropLet(name, tail.head));
        }

        public static CpsOp Annotate(int line, CpsOp body) {
            return body; // TODO: implement me
        }

        public static CpsOp LexAccess(Lexical l, int up, CpsOp[] zyg) {
            return Primitive(zyg, delegate(ClrOp[] heads) {
                return (heads.Length >= 1) ? l.SetCode(up, heads[0]) :
                    l.GetCode(up);
            });
        }
    }

    class NamProcessor {
        StaticSub sub;
        Dictionary<string, Type> let_types = new Dictionary<string, Type>();

        public NamProcessor(StaticSub sub) {
            this.sub = sub;
        }

        CpsOp AccessLex(bool core, object[] zyg) {
            string name = ((JScalar)zyg[1]).str;
            CpsOp set_to = (zyg.Length > 2) ? Scan(zyg[2]) : null;

            Type t;
            if (!core && let_types.TryGetValue(name, out t)) {
                return (set_to == null) ? CpsOp.PeekLet(name, t) :
                    CpsOp.PokeLet(name, new CpsOp[1] { set_to });
            }

            int uplevel;
            Lexical lex = ResolveLex(name, out uplevel, core);

            return CpsOp.LexAccess(lex, uplevel,
                set_to == null ? new CpsOp[0] : new CpsOp[] { set_to });
        }

        Lexical ResolveLex(string name, out int uplevel, bool core) {
            uplevel = 0;
            StaticSub csr = sub;

            while (true) {
                Lexical r;
                if ((!core || csr.IsCore()) &&
                        csr.l_lexicals.TryGetValue(name, out r)) {
                    if (r is LexAlias) {
                        name = (r as LexAlias).to;
                    } else {
                        return r;
                    }
                }
                if (csr.outer == null)
                    throw new Exception("Unable to find lexical " + name + " in " + sub.name);
                csr = (StaticSub) csr.outer.Resolve();
                uplevel++;
            }
        }

        static Dictionary<string, Func<NamProcessor, object[], CpsOp>> handlers;

        static NamProcessor() {
            handlers = new Dictionary<string, Func<NamProcessor,object[],CpsOp>>();
            Dictionary<string, Func<CpsOp[], CpsOp>>
                thandlers = new Dictionary<string, Func<CpsOp[], CpsOp>>();
            Dictionary<string, Func<object[], object>> mhandlers =
                new Dictionary<string, Func<object[], object>>();


            handlers["ann"] = delegate(NamProcessor th, object[] zyg) {
                return CpsOp.Annotate((int) ((JScalar)zyg[2]).num, th.Scan(zyg[3])); };
            handlers["scopedlex"] = delegate(NamProcessor th, object[] zyg) {
                return th.AccessLex(false, zyg); };
            handlers["corelex"] = delegate(NamProcessor th, object[] zyg) {
                return th.AccessLex(true, zyg); };
            handlers["letn"] = delegate(NamProcessor th, object[] zyg) {
                int i = 1;
                Dictionary<string,Type> old =
                    new Dictionary<string,Type>(th.let_types);
                List<KeyValuePair<string,CpsOp>> lvec =
                    new List<KeyValuePair<string,CpsOp>>();
                while (zyg.Length - i >= 3 && zyg[i] is JScalar) {
                    string name = ((JScalar)zyg[i]).str;
                    CpsOp  init = th.Scan(zyg[i+1]);
                    th.let_types[name] = init.head.Returns;
                    lvec.Add(new KeyValuePair<string,CpsOp>(name, init));
                    i += 2;
                }
                List<CpsOp> rest = new List<CpsOp>();
                while (i < zyg.Length)
                    rest.Add(th.Scan(zyg[i++]));
                CpsOp bit = CpsOp.Sequence(rest.ToArray());
                for (int j = lvec.Count - 1; j >= 0; j--)
                    bit = CpsOp.Let(lvec[j].Key, lvec[j].Value, bit);
                th.let_types = old;
                return bit;
            };

            thandlers["prog"] = CpsOp.Sequence;
            thandlers["bif_defined"] = Contexty("mro_defined");
            thandlers["bif_bool"] = Contexty("mro_Bool");
            thandlers["bif_num"] = Contexty("mro_Numeric");
            thandlers["bif_str"] = Contexty("mro_Str");

            foreach (KeyValuePair<string, Func<CpsOp[], CpsOp>> kv
                    in thandlers) {
                handlers[kv.Key] = MakeTotalHandler(kv.Value);
            }
            foreach (KeyValuePair<string, Func<object[], object>> kv
                    in mhandlers) {
                handlers[kv.Key] = MakeMacroHandler(kv.Value);
            }
        }

        static Func<CpsOp[], CpsOp> Contexty(string name) {
            FieldInfo f = Tokens.DynMetaObject.GetField(name);
            MethodInfo g = f.FieldType.GetMethod("Get");
            return delegate(CpsOp[] cpses) {
                return CpsOp.Contexty(f, g, cpses);
            };
        }

        static Func<NamProcessor, object[], CpsOp> MakeTotalHandler(
                Func<CpsOp[], CpsOp> real) {
            return delegate (NamProcessor th, object[] zyg) {
                CpsOp[] zco = new CpsOp[zyg.Length - 1];
                for (int i = 0; i < zco.Length; i++)
                    zco[i] = th.Scan(zyg[i+1]);
                return real(zco);
            };
        }

        static Func<NamProcessor, object[], CpsOp> MakeMacroHandler(
                Func<object[], object> real) {
            return delegate (NamProcessor th, object[] zyg) {
                return th.Scan(real(zyg)); };
        }

        public CpsOp MakeBody() { return Scan(WrapBody()); }

        object WrapBody() {
            // XXX returnable, enter code, etc etc
            return sub.body;
        }

        CpsOp Scan(object node) {
            object[] rnode = (object[]) node;
            string tag = ((JScalar)rnode[0]).str;
            Func<NamProcessor, object[], CpsOp> handler;
            if (!handlers.TryGetValue(tag, out handler))
                throw new Exception("Unhandled nam operator " + tag);
            return handler(this, rnode);
        }
    }

    public class CLRBackend {
        AssemblyBuilder ab;
        ModuleBuilder mob;
        TypeBuilder tb;

        Unit unit;

        CLRBackend(string dir, string mobname, string filename) {
            AssemblyName an = new AssemblyName(mobname);
            ab = AppDomain.CurrentDomain.DefineDynamicAssembly(an,
                    AssemblyBuilderAccess.Save, dir);
            mob = ab.DefineDynamicModule(mobname, filename);

            tb = mob.DefineType(mobname, TypeAttributes.Public |
                    TypeAttributes.Sealed | TypeAttributes.Abstract |
                    TypeAttributes.Class | TypeAttributes.BeforeFieldInit);
        }

        void Process(Unit unit) {
            this.unit = unit;

            unit.BindFields(delegate(string name, Type type) {
                return tb.DefineField(name, type, FieldAttributes.Public |
                    FieldAttributes.Static);
            });

            unit.VisitSubsPostorder(delegate(int ix, StaticSub obj) {
                NamProcessor np = new NamProcessor(obj);
                MethodInfo mi = DefineCpsMethod(
                    Unit.SharedName('C', ix, obj.name), false,
                    np.MakeBody());
            });

            unit.VisitSubsPostorder(delegate(int ix, StaticSub obj) {
                // TODO append chunks to Thaw here for sub2 stuff
            });

            unit.VisitSubsPostorder(delegate(int ix, StaticSub obj) {
                // TODO append chunks to Thaw here for sub3 stuff
            });

            // TODO generate BOOT method here
        }

        void Finish(string filename) {
            tb.CreateType();

            ab.Save(filename);
        }

        MethodInfo DefineCpsMethod(string name, bool pub, CpsOp body) {
            MethodBuilder mb = tb.DefineMethod(name, MethodAttributes.Static |
                    (pub ? MethodAttributes.Public : 0),
                    typeof(Frame), new Type[] { typeof(Frame) });
            CgContext cx = new CgContext();
            // ListCases may want to define labels, so this needs to come
            // early
            cx.il = mb.GetILGenerator();
            cx.num_cases = 1;

            foreach (ClrOp s in body.stmts)
                s.ListCases(cx);
            body.head.ListCases(cx);

            cx.cases = new Label[cx.num_cases];
            for (int i = 0; i < cx.num_cases; i++)
                cx.cases[i] = cx.il.DefineLabel();

            cx.il.Emit(OpCodes.Ldarg_0);
            cx.il.Emit(OpCodes.Ldfld, Tokens.Frame_ip);
            cx.il.Emit(OpCodes.Switch, cx.cases);

            cx.il.Emit(OpCodes.Ldarg_0);
            cx.il.Emit(OpCodes.Ldstr, "Invalid IP");
            cx.il.Emit(OpCodes.Call, Tokens.Kernel_Die);
            cx.il.Emit(OpCodes.Ret);

            cx.il.MarkLabel(cx.cases[cx.next_case++]);
            foreach (ClrOp s in body.stmts)
                s.CodeGen(cx);
            body.head.CodeGen(cx);

            return mb;
        }

        void DefineMainMethod(MethodInfo boot) {
            MethodBuilder mb = tb.DefineMethod("Main", MethodAttributes.Static |
                    MethodAttributes.Public, typeof(void),
                    new Type[] { typeof(string[]) });
            ILGenerator il = mb.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldftn, boot);
            il.Emit(OpCodes.Newobj, Tokens.DynBlockDelegate_ctor);
            il.Emit(OpCodes.Call, Tokens.Kernel_RunLoop);
            il.Emit(OpCodes.Ret);

            ab.SetEntryPoint(mb);
        }

        [ThreadStatic] static Dictionary<string, Unit> used_units;
        internal static object Resolve(Xref x) {
            return used_units[x.unit].xref[x.index];
        }

        public static void Main() {
            Directory.SetCurrentDirectory("obj");
            CLRBackend c = new CLRBackend(null, "SAFE", "SAFE.dll");

            string tx = File.ReadAllText("SAFE.nam");
            Unit root = new Unit((object[])Reader.Read(tx));
            used_units = new Dictionary<string, Unit>();
            used_units["SAFE"] = root;

            c.Process(root);

            c.Finish("SAFE.dll");
        }
    }
}
