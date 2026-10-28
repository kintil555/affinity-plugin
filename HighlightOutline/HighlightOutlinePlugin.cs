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

            // Layer context menu: add Tools > Highlight Outline
            context.Patch("Layer context menu", h =>
            {
                var t = Asm(AffinityAsm).GetType("Serif.Affinity.UI.Studio.LayerPage", true);
                var m = t.GetMethod("GetContextMenu", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                h.Patch(m, postfix: new HarmonyMethod(typeof(HighlightOutlinePlugin), nameof(GetContextMenu_Postfix)));
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

        public static void GetContextMenu_Postfix(ref ContextMenu __result)
        {
            try
            {
                if (__result == null) return;
                if (__result.Items.OfType<MenuItem>().Any(i => (string)i.Tag == "apl-highlight")) return;

                var tools = new MenuItem { Header = "Tools", Tag = "apl-highlight" };
                var item = new MenuItem { Header = "Highlight Outline" };
                item.Click += (s, e) => OpenPanel();
                tools.Items.Add(item);
                __result.Items.Add(new Separator());
                __result.Items.Add(tools);
            }
            catch (Exception ex) { Ctx?.LogError("Context menu patch failed", ex); }
        }

        static void OpenPanel()
        {
            try { new HighlightPanel().Show(); }
            catch (Exception ex) { Ctx?.LogError("Open panel failed", ex); }
        }

        internal static Assembly Asm(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == name);

        internal static Type PType(string full) => Asm(PersonaAsm).GetType(full, true);
    }

    /// <summary>Wraps Affinity's EffectsDataSource to edit Inner Shadow on the current selection.</summary>
    internal sealed class FxSession : IDisposable
    {
        readonly object _ds;
        readonly object _item;
        readonly Type _dsType;

        public FxSession()
        {
            var app = Application.Current;
            var viewSvcType = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Services.IDocumentViewService");
            var getSvc = app.GetType().GetMethods()
                .First(m => m.Name == "GetService" && m.IsGenericMethod && m.GetParameters().Length == 0)
                .MakeGenericMethod(viewSvcType);
            var viewSvc = getSvc.Invoke(app, null);
            var doc = viewSvcType.GetProperty("CurrentDocument").GetValue(viewSvc);
            var selection = doc.GetType().GetProperty("Selection").GetValue(doc);

            _dsType = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Data.EffectsDataSource");
            _ds = Activator.CreateInstance(_dsType, app, false);
            _dsType.GetMethod("Initialise").Invoke(_ds, new[] { selection });
            _dsType.GetMethod("Activate").Invoke(_ds, null);

            var effects = (IEnumerable)_dsType.GetProperty("Effects").GetValue(_ds);
            _item = effects.Cast<object>().First(e => e.GetType().Name == "EffectsDataSourceItemInnerShadow");
            _dsType.GetMethod("EnableEffect").Invoke(_ds, new[] { _item });

            // Fill opacity 0%
            var uv = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Units.UnitValue");
            var ut = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Units.UnitType");
            var zero = Activator.CreateInstance(uv, 0.0, Enum.Parse(ut, "Percentage"));
            _dsType.GetProperty("FillOpacity").SetValue(_ds, zero);
        }

        object Param(string typeName) =>
            ((IEnumerable)_item.GetType().GetProperty("Params").GetValue(_item))
                .Cast<object>().FirstOrDefault(p => p.GetType().Name == typeName);

        void Edit(object p, Action set)
        {
            _dsType.GetMethod("BeginEditEffect").Invoke(_ds, new[] { p });
            set();
            _dsType.GetMethod("ContinueEditEffect").Invoke(_ds, new[] { p });
            _dsType.GetMethod("EndEditEffect").Invoke(_ds, new[] { p });
        }

        public void SetRadius(double v) { var p = Param("EffectsDataSourceItemRadiusParam"); Edit(p, () => p.GetType().GetProperty("Value").SetValue(p, v)); }
        public void SetIntensity(double v) { var p = Param("EffectsDataSourceItemIntensityParam"); Edit(p, () => p.GetType().GetProperty("Value").SetValue(p, v)); }
        // Value1 = offset, Value2 = angle (order assumed; verify in-app)
        public void SetOffset(double v) { var p = Param("EffectsDataSourceItemOffsetAngleParam"); Edit(p, () => p.GetType().GetProperty("Value1").SetValue(p, v)); }

        public void Dispose()
        {
            try { _dsType.GetMethod("Deactivate").Invoke(_ds, null); } catch { }
            (_ds as IDisposable)?.Dispose();
        }
    }

    internal sealed class HighlightPanel : Window
    {
        readonly FxSession _fx;
        readonly TextBox _radius = new TextBox { Text = "0" };
        readonly TextBox _offset = new TextBox { Text = "10" };
        readonly TextBox _intensity = new TextBox { Text = "0" };

        public HighlightPanel()
        {
            Title = "Highlight Outline";
            Width = 260; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize; Topmost = true;
            Owner = Application.Current.MainWindow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new StackPanel { Margin = new Thickness(12) };
            Row(panel, "Radius", _radius);
            Row(panel, "Offset", _offset);
            Row(panel, "Intensity", _intensity);
            var apply = new Button { Content = "Apply", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(0, 4, 0, 4) };
            apply.Click += (s, e) => Apply();
            panel.Children.Add(apply);
            Content = panel;

            _fx = new FxSession();   // adds Inner Shadow + fill opacity 0
            Push(_radius, _fx.SetRadius);
            Push(_offset, _fx.SetOffset);
            Push(_intensity, _fx.SetIntensity);
            _radius.TextChanged += (s, e) => Push(_radius, _fx.SetRadius);
            _offset.TextChanged += (s, e) => Push(_offset, _fx.SetOffset);
            _intensity.TextChanged += (s, e) => Push(_intensity, _fx.SetIntensity);
            Closed += (s, e) => _fx.Dispose();
        }

        static void Row(Panel p, string label, TextBox box)
        {
            p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 2) });
            p.Children.Add(box);
        }

        static void Push(TextBox box, Action<double> setter)
        {
            if (double.TryParse(box.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                try { setter(v); }
                catch (Exception ex) { HighlightOutlinePlugin.Ctx?.LogError("Set FX value failed", ex); }
            }
        }

        void Apply()
        {
            try
            {
                var t = HighlightOutlinePlugin.PType("Serif.Interop.Persona.Commands.RasteriseCommand");
                var cmd = Activator.CreateInstance(t);
                var canExec = (bool)t.GetMethod("CanExecute").Invoke(cmd, new object[] { null });
                if (!canExec) { HighlightOutlinePlugin.Ctx?.LogWarning("Rasterise not available for selection"); return; }

                HighlightOutlinePlugin.SuppressRasteriseDialog = true;
                try { t.GetMethod("Execute").Invoke(cmd, new object[] { null }); }
                finally { HighlightOutlinePlugin.SuppressRasteriseDialog = false; }
                Close();
            }
            catch (Exception ex) { HighlightOutlinePlugin.Ctx?.LogError("Apply failed", ex); }
        }
    }
}
