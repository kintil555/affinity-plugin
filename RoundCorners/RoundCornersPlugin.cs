using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HarmonyLib;
using AffinityPluginLoader;

namespace RoundCorners
{
    /// <summary>
    /// Right-click a pixel layer > Round Corners: wraps it in a same-size picture frame (vector shape),
    /// so the corners can be rounded with Affinity's normal Corner tool and the image follows.
    /// </summary>
    public class RoundCornersPlugin : AffinityPlugin
    {
        internal static IPluginContext Ctx;
        const string PersonaAsm = "Serif.Interop.Persona";
        const string AffinityAsm = "Serif.Affinity";

        public override void OnPatch(Harmony harmony, IPluginContext context)
        {
            Ctx = context;
            context.Patch("Layer context menu", h =>
            {
                var t = Asm(AffinityAsm).GetType("Serif.Affinity.UI.AffinityContextMenu", true);
                var m = t.GetMethod("OnOpened", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                h.Patch(m, prefix: new HarmonyMethod(typeof(RoundCornersPlugin), nameof(OnOpened_Prefix)));
                context.Log("Patched AffinityContextMenu.OnOpened");
            });
        }

        static object _item;
        static int _diag;

        // Same approach as HighlightOutline: copy the read-only ItemsSource, insert a real WorkspaceMenuItem after "Duplicate".
        public static void OnOpened_Prefix(ContextMenu __instance)
        {
            try
            {
                var wmi = Asm(AffinityAsm).GetType("Serif.Affinity.Workspaces.WorkspaceMenuItem", true);
                var src = (__instance.ItemsSource as IEnumerable) ?? __instance.Items;
                var items = src.Cast<object>().Where(o => o != null).ToList();
                if (items.Contains(_item)) return;

                var textProp = wmi.GetProperty("Text");
                var cmdProp = wmi.GetProperty("Command");
                // Match like HighlightOutline does: by command type first (Text can be localised/null), then by text.
                int dup = items.FindIndex(o =>
                {
                    if (!wmi.IsInstanceOfType(o)) return false;
                    var c = cmdProp.GetValue(o);
                    return (c != null && c.GetType().Name == "DuplicateObjectsIgnorePixelSelectionCommand")
                        || (string)textProp.GetValue(o) == "Duplicate";
                });
                if (dup < 0)
                {
                    if (_diag++ < 3)
                        Ctx?.Log("Menu not matched. Items: " + string.Join(" | ", items.Select(o => wmi.IsInstanceOfType(o) ? (string)textProp.GetValue(o) ?? "<null>" : o.GetType().Name)));
                    return;
                }

                if (_item == null)
                {
                    var cmdType = PType("Serif.Interop.Persona.Commands.Command");
                    var cmd = Activator.CreateInstance(MakeCommandType(cmdType));
                    _item = wmi.GetConstructor(new[] { typeof(string), cmdType }).Invoke(new[] { "Round Corners (Frame)", cmd });
                }

                var list = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(wmi));
                for (int i = 0; i < items.Count; i++) { list.Add(items[i]); if (i == dup) list.Add(_item); }
                __instance.ItemsSource = list;
            }
            catch (Exception ex) { Ctx?.LogError("Context menu patch failed", ex); }
        }

        static Type MakeCommandType(Type baseType)
        {
            var ab = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("RoundCorners.Dyn"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var tb = ab.DefineDynamicModule("m").DefineType("RoundCornersCommand", TypeAttributes.Public | TypeAttributes.Class, baseType);
            var baseCtor = baseType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
            var il = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes).GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Call, baseCtor);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);

            var attrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig;
            il = tb.DefineMethod("Execute", attrs, typeof(void), new[] { typeof(object) }).GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(RoundCornersPlugin).GetMethod(nameof(OpenBar), BindingFlags.Public | BindingFlags.Static));
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            il = tb.DefineMethod("CanExecute", attrs, typeof(bool), new[] { typeof(object) }).GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return tb.CreateType();
        }

        static bool _busy;

        // Menu action: pixel layer -> vector rectangle (same size) -> picture frame holding the pixels.
        // The frame is a normal vector shape, so Affinity's own Corner tool rounds it and the content follows.
        public static void OpenBar()
        {
            if (_busy) return;
            _busy = true;
            var steps = new[]
            {
                "CreatePixelSelectionFromObjectCommand",   // layer pixels -> selection (rectangle for a plain image layer)
                "CopyCommand",                              // keep the pixels
                "DeleteObjectsCommand",                     // remove the original layer (it is pasted back inside the frame)
                "NewVectorLayerFromSelectionCommand",       // selection -> vector shape of the same size
                "ConvertToPictureFrameCommand",             // shape -> picture frame
                "PasteIntoFrameCommand",                    // pixels -> inside the frame
            };
            RunSteps(steps, 0);
        }

        static void RunSteps(string[] steps, int i)
        {
            if (i >= steps.Length) { _busy = false; Ctx?.Log("Frame pipeline finished"); return; }
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!Run(steps[i])) { Ctx?.LogWarning("Stopped at step " + (i + 1) + ": " + steps[i]); _busy = false; return; }
                    RunSteps(steps, i + 1);
                }
                catch (Exception ex) { Ctx?.LogError("Step failed: " + steps[i], ex.InnerException ?? ex); _busy = false; }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Runs a Persona command by type name; logs and returns false when unavailable.
        static bool Run(string commandType)
        {
            var t = PType("Serif.Interop.Persona.Commands." + commandType);
            var cmd = Activator.CreateInstance(t);
            var can = t.GetMethod("CanExecute");
            if (can != null && !(bool)can.Invoke(cmd, new object[] { null })) { Ctx?.LogWarning(commandType + ": CanExecute = false"); return false; }
            t.GetMethod("Execute").Invoke(cmd, new object[] { null });
            Ctx?.Log("Ran " + commandType);
            return true;
        }

        internal static Assembly Asm(string name) => AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == name);
        internal static Type PType(string full) => Asm(PersonaAsm).GetType(full, true);
    }
}
