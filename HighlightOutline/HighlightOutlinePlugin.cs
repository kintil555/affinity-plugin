using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HarmonyLib;
using AffinityPluginLoader;

namespace HighlightOutline
{
    public class HighlightOutlinePlugin : AffinityPlugin
    {
        internal static IPluginContext Ctx;
        internal static bool SuppressRasteriseDialog;

        const string PersonaAsm = "Serif.Interop.Persona";
        const string AffinityAsm = "Serif.Affinity";

        public override void OnPatch(Harmony harmony, IPluginContext context)
        {
            Ctx = context;

            // Right-click menu (AffinityContextMenu): remove our items before UpdateState, add after
            context.Patch("Layer context menu", h =>
            {
                var t = Asm(AffinityAsm).GetType("Serif.Affinity.UI.AffinityContextMenu", true);
                var m = t.GetMethod("OnOpened", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                h.Patch(m, prefix: new HarmonyMethod(typeof(HighlightOutlinePlugin), nameof(OnOpened_Prefix)));
                context.Log("Patched AffinityContextMenu.OnOpened");
            });

            // Skip the rasterise dialog (Preserve Layer FX = off) when we trigger it
            context.Patch("Rasterise dialog", h =>
            {
                var t = Asm(AffinityAsm).GetType("Serif.Affinity.Services.UserInterfaceService", true);
                var m = t.GetMethod("ShowRasteriseDialog", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                h.Patch(m, prefix: new HarmonyMethod(typeof(HighlightOutlinePlugin), nameof(ShowRasteriseDialog_Prefix)));
            });
        }

        // bool ShowRasteriseDialog(ref bool preserveLayerFx, ref int resample, bool fxEnabled, bool resamplingEnabled)
        public static bool ShowRasteriseDialog_Prefix(ref bool __0, ref bool __result)
        {
            if (!SuppressRasteriseDialog) return true;
            __0 = false;        // do not preserve layer FX
            __result = true;    // dialog "accepted" (assumed true = OK)
            return false;
        }

        static object _toolsItem;   // our WorkspaceMenuItem (reused)
        static int _diagLogged;

        // The layer menu's ItemsSource is a ReadOnlyCollection (LayerView.TryMakeMenu uses List.AsReadOnly()),
        // so it cannot be modified in place. Copy it into a new List, insert our item, and swap ItemsSource.
        // UpdateState (runs in base OnOpened path) casts each item to WorkspaceMenuItem, so we use a real one.
        public static void OnOpened_Prefix(ContextMenu __instance)
        {
            try
            {
                var wmiType = Asm(AffinityAsm).GetType("Serif.Affinity.Workspaces.WorkspaceMenuItem", true);
                var src = (__instance.ItemsSource as IEnumerable) ?? __instance.Items;
                var items = src.Cast<object>().Where(o => o != null).ToList();
                if (items.Contains(_toolsItem)) return;

                var textProp = wmiType.GetProperty("Text");
                var cmdProp = wmiType.GetProperty("Command");
                int dup = items.FindIndex(o =>
                {
                    if (!wmiType.IsInstanceOfType(o)) return false;
                    var cmd = cmdProp.GetValue(o);
                    return (cmd != null && cmd.GetType().Name == "DuplicateObjectsIgnorePixelSelectionCommand")
                        || (string)textProp.GetValue(o) == "Duplicate";
                });
                if (dup < 0)
                {
                    if (_diagLogged++ < 3)
                        Ctx?.Log("Menu not matched. Items: " + string.Join(" | ",
                            items.Select(o => wmiType.IsInstanceOfType(o) ? (string)textProp.GetValue(o) ?? "<null>" : o.GetType().Name)));
                    return;
                }

                if (_toolsItem == null) _toolsItem = BuildToolsItem(wmiType);

                var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(wmiType);
                var newList = (IList)Activator.CreateInstance(listType);
                for (int i = 0; i < items.Count; i++)
                {
                    newList.Add(items[i]);
                    if (i == dup) newList.Add(_toolsItem);
                }
                __instance.ItemsSource = newList;
            }
            catch (Exception ex) { Ctx?.LogError("Context menu patch failed", ex); }
        }

        static object BuildToolsItem(Type wmiType)
        {
            var cmdType = PType("Serif.Interop.Persona.Commands.Command");
            var cmd = Activator.CreateInstance(MakeCommandType(cmdType));

            var child = wmiType.GetConstructor(new[] { typeof(string), cmdType }).Invoke(new[] { "Highlight Outline", cmd });
            var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(wmiType);
            var children = (IList)Activator.CreateInstance(listType);
            children.Add(child);
            var ilist = typeof(System.Collections.Generic.IList<>).MakeGenericType(wmiType);
            return wmiType.GetConstructor(new[] { typeof(string), ilist }).Invoke(new object[] { "Tools", children });
        }

        // Runtime subclass of Serif's abstract Command: Execute opens our panel.
        static Type MakeCommandType(Type baseType)
        {
            var ab = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("HighlightOutline.Dyn"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var mb = ab.DefineDynamicModule("m");
            var tb = mb.DefineType("HighlightOutlineCommand", TypeAttributes.Public | TypeAttributes.Class, baseType);

            var baseCtor = baseType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
            var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var il = ctor.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Call, baseCtor);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);

            var attrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig;
            var exec = tb.DefineMethod("Execute", attrs, typeof(void), new[] { typeof(object) });
            il = exec.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(HighlightOutlinePlugin).GetMethod(nameof(OpenPanel), BindingFlags.Public | BindingFlags.Static));
            il.Emit(System.Reflection.Emit.OpCodes.Ret);

            var can = tb.DefineMethod("CanExecute", attrs, typeof(bool), new[] { typeof(object) });
            il = can.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);

            return tb.CreateType();
        }

        static object _dialog;      // Affinity's native LayerEffectsDialog
        static bool _applyRequested;

        // Opens Affinity's own Layer Effects dialog (native UI: colour picker, opacity, angle, radius...)
        // on the current selection with Inner Shadow preselected and our defaults applied.
        public static void OpenPanel()
        {
            try
            {
                var selection = GetSelection();
                var shadowType = PType("Serif.Interop.Persona.Data.EffectsDataSourceItemInnerShadow");

                var panelIface = FindType("IPanelService");
                var panelSvc = GetService(panelIface);
                panelSvc.GetType().GetMethod("ShowLayerEffectsDialog").Invoke(panelSvc, new object[] { selection, shadowType });

                const BindingFlags nb = BindingFlags.NonPublic | BindingFlags.Instance;
                var dlg = panelSvc.GetType().GetField("layerEffectsDialog", nb).GetValue(panelSvc);
                var ds = dlg.GetType().GetField("_dataSource", nb).GetValue(dlg);
                if (ReferenceEquals(dlg, _dialog)) return;     // already open and initialised
                _dialog = dlg;

                var win = (Window)dlg;
                HookApplyButton(win);
                var fx = new FxSession(ds);
                fx.ApplyDefaults();                         // now
                win.Dispatcher.BeginInvoke(new Action(fx.ApplyDefaults),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);   // again once the UI has bound
            }
            catch (Exception ex) { Ctx?.LogError("Open panel failed", ex); }
        }

        // Reuse the dialog's accept button as "Apply": it closes the dialog (committing the FX),
        // then we rasterise without preserving layer FX.
        static void HookApplyButton(Window dlg)
        {
            var btn = dlg.GetType().GetField("AcceptButton", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                         ?.GetValue(dlg) as Button;
            if (btn != null)
            {
                btn.Content = "Apply";
                btn.Click += (s, e) => _applyRequested = true;
            }
            else Ctx?.LogWarning("Dialog accept button not found; closing the dialog will not rasterise");

            dlg.Closed += (s, e) =>
            {
                _dialog = null;
                if (!_applyRequested) return;
                _applyRequested = false;
                dlg.Dispatcher.BeginInvoke(new Action(RasteriseSelection));
            };
        }

        // Rasterise with "Preserve Layer FX" off so the FX become plain pixels.
        static void RasteriseSelection()
        {
            try
            {
                var t = PType("Serif.Interop.Persona.Commands.RasteriseCommand");
                var cmd = Activator.CreateInstance(t);
                if (!(bool)t.GetMethod("CanExecute").Invoke(cmd, new object[] { null }))
                { Ctx?.LogWarning("Rasterise not available for selection"); return; }

                SuppressRasteriseDialog = true;
                try { t.GetMethod("Execute").Invoke(cmd, new object[] { null }); }
                finally { SuppressRasteriseDialog = false; }
            }
            catch (Exception ex) { Ctx?.LogError("Rasterise failed", ex); }
        }

        internal static object GetService(Type serviceType)
        {
            var app = Application.Current;
            var m = app.GetType().GetMethods()
                .First(x => x.Name == "GetService" && x.IsGenericMethod && x.GetParameters().Length == 0)
                .MakeGenericMethod(serviceType);
            return m.Invoke(app, null);
        }

        internal static object GetSelection()
        {
            var viewSvc = GetService(PType("Serif.Interop.Persona.Services.IDocumentViewService"));
            var doc = viewSvc.GetType().GetProperty("CurrentDocument").GetValue(viewSvc);
            return doc.GetType().GetProperty("Selection").GetValue(doc);
        }

        internal static Type FindType(string name)
        {
            foreach (var asm in new[] { Asm(AffinityAsm), Asm(PersonaAsm) })
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                var t = types.FirstOrDefault(x => x.Name == name);
                if (t != null) return t;
            }
            throw new TypeLoadException(name);
        }

        internal static Assembly Asm(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == name);

        internal static Type PType(string full) => Asm(PersonaAsm).GetType(full, true);
    }

    /// <summary>Wraps Affinity's EffectsDataSource to edit Inner Shadow on the current selection.</summary>
    internal sealed class FxSession
    {
        readonly object _ds;
        readonly object _item;
        readonly Type _dsType;

        public FxSession(object dataSource)
        {
            _ds = dataSource;
            _dsType = _ds.GetType();
            var effects = (IEnumerable)_dsType.GetProperty("Effects").GetValue(_ds);
            _item = effects.Cast<object>().First(e => e.GetType().Name == "EffectsDataSourceItemInnerShadow");
        }

        // Defaults (all editable afterwards in the dialog).
        const double EffectOpacity = 1.0;   // "Opacity" slider of the Inner Shadow, 0..1 (100%)
        const double FillOpacityPct = 0.0;  // "Fill Opacity" of the layer (0%)
        const double Radius = 0, Offset = 10, Intensity = 0;

        /// <summary>Enable Inner Shadow and set: opacity 100%, radius 0, offset 10, intensity 0, fill opacity 0%.</summary>
        public void ApplyDefaults()
        {
            Step("enable effect", () =>
            {
                var enabled = (bool)HighlightOutlinePlugin.PType("Serif.Interop.Persona.Data.IEffectsDataSourceItem")
                    .GetProperty("IsEnabled").GetValue(_item);
                if (!enabled) _dsType.GetMethod("EnableEffect").Invoke(_ds, new[] { _item });
            });
            Step("fill opacity", SetFillOpacity);
            Step("opacity", () => SetParam("EffectsDataSourceItemOpacityParam", "Value", EffectOpacity));
            Step("radius", () => SetParam("EffectsDataSourceItemRadiusParam", "Value", Radius));
            Step("offset", () =>
            {
                if (Param("EffectsDataSourceItemOffsetParam") != null) SetParam("EffectsDataSourceItemOffsetParam", "Value", Offset);
                else SetParam("EffectsDataSourceItemOffsetAngleParam", "Value1", Offset);   // Value1 = offset, Value2 = angle
            });
            Step("intensity", () => SetParam("EffectsDataSourceItemIntensityParam", "Value", Intensity));
        }

        static void Step(string name, Action a)
        {
            try { a(); }
            catch (Exception ex) { HighlightOutlinePlugin.Ctx?.LogError("Default '" + name + "' failed", ex); }
        }

        void SetFillOpacity()
        {
            var uv = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Units.UnitValue");
            var ut = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Units.UnitType");
            // UnitValue has two (double, UnitType) ctors differing only by modopt, so pick one explicitly.
            var ctor = uv.GetConstructors().First(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == 2 && ps[0].ParameterType == typeof(double) && ps[1].ParameterType == ut;
            });
            var val = ctor.Invoke(new object[] { FillOpacityPct, Enum.Parse(ut, "Percentage") });
            _dsType.GetProperty("FillOpacity").SetValue(_ds, val);
        }

        // Params the dialog UI is bound to (CurrentParams); fall back to the item's full list.
        System.Collections.Generic.IEnumerable<object> Params()
        {
            var cur = (_dsType.GetProperty("CurrentParams")?.GetValue(_ds) as IEnumerable)?.Cast<object>().ToList();
            if (cur != null && cur.Count > 0) return cur;
            return ((IEnumerable)HighlightOutlinePlugin.PType("Serif.Interop.Persona.Data.IEffectsDataSourceItem")
                .GetProperty("Params").GetValue(_item)).Cast<object>();
        }

        object Param(string typeName) => Params().FirstOrDefault(p => p.GetType().Name == typeName);

        void SetParam(string typeName, string prop, double v)
        {
            var p = Param(typeName);
            if (p == null) { HighlightOutlinePlugin.Ctx?.LogWarning("Param not found: " + typeName); return; }
            _dsType.GetMethod("BeginEditEffect").Invoke(_ds, new[] { p });
            p.GetType().GetProperty(prop).SetValue(p, v);
            _dsType.GetMethod("ContinueEditEffect").Invoke(_ds, new[] { p });
            _dsType.GetMethod("EndEditEffect").Invoke(_ds, new[] { p });
        }
    }
}
