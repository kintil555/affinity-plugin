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
    /// Rounds the corners of a plain pixel layer: layer pixels -> pixel selection -> Smooth(radius) -> mask.
    /// Right-click layer > Tools > Round Corners opens a small bar with a radius slider and Apply.
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
                int dup = items.FindIndex(o => wmi.IsInstanceOfType(o) && (string)textProp.GetValue(o) == "Duplicate");
                if (dup < 0) return;

                if (_item == null)
                {
                    var cmdType = PType("Serif.Interop.Persona.Commands.Command");
                    var cmd = Activator.CreateInstance(MakeCommandType(cmdType));
                    _item = wmi.GetConstructor(new[] { typeof(string), cmdType }).Invoke(new[] { "Round Corners", cmd });
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

        static Window _bar;

        public static void OpenBar()
        {
            try
            {
                if (_bar != null) { _bar.Activate(); return; }
                _bar = RoundBar.Build(ApplyRadius);
                _bar.Closed += (s, e) => _bar = null;
                _bar.Show();
            }
            catch (Exception ex) { _bar = null; Ctx?.LogError("Open bar failed", ex); }
        }

        // Runs a Persona command by type name; logs and returns false when unavailable.
        static bool Run(string commandType)
        {
            var t = PType("Serif.Interop.Persona.Commands." + commandType);
            var cmd = Activator.CreateInstance(t);
            var can = t.GetMethod("CanExecute");
            if (can != null && !(bool)can.Invoke(cmd, new object[] { null })) { Ctx?.LogWarning(commandType + " not available for this selection"); return false; }
            t.GetMethod("Execute").Invoke(cmd, new object[] { null });
            Ctx?.Log("Ran " + commandType);
            return true;
        }

        // layer pixels -> selection -> smooth(radius) -> mask
        internal static void ApplyRadius(double radius)
        {
            try
            {
                if (!Run("CreatePixelSelectionFromObjectCommand")) return;
                if (radius > 0 && !SmoothSelection(radius)) return;
                Run("NewMaskPixelLayerCommand");
            }
            catch (Exception ex) { Ctx?.LogError("Round corners failed", ex.InnerException ?? ex); }
        }

        // Drives Affinity's own Smooth Selection (ModifySelectionDataSource) with the given radius.
        static bool SmoothSelection(double radius)
        {
            try
            {
                var dsType = PType("Serif.Interop.Persona.Data.ModifySelectionDataSource");
                object ds = null;
                foreach (var c in dsType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var ps = c.GetParameters();
                    try
                    {
                        if (ps.Length == 0) ds = c.Invoke(null);
                        else if (ps.Length == 1) ds = c.Invoke(new object[] { Application.Current });
                        if (ds != null) break;
                    }
                    catch { }
                }
                if (ds == null)
                {
                    Ctx?.LogWarning("ModifySelectionDataSource ctors: " + string.Join(" | ", dsType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Select(c => string.Join(",", c.GetParameters().Select(p => p.ParameterType.Name)))));
                    return false;
                }

                var toolProp = dsType.GetProperty("ToolType");
                if (toolProp != null && toolProp.PropertyType.IsEnum)
                    toolProp.SetValue(ds, Enum.Parse(toolProp.PropertyType, "Smooth"));
                dsType.GetProperty("Radius").SetValue(ds, Convert.ChangeType(radius, dsType.GetProperty("Radius").PropertyType));
                var apply = dsType.GetMethod("OnApply", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                apply.Invoke(ds, apply.GetParameters().Length == 0 ? null : new object[apply.GetParameters().Length]);
                (ds as IDisposable)?.Dispose();
                return true;
            }
            catch (Exception ex) { Ctx?.LogError("Smooth selection failed", ex.InnerException ?? ex); return false; }
        }

        internal static Assembly Asm(string name) => AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == name);
        internal static Type PType(string full) => Asm(PersonaAsm).GetType(full, true);
    }

    internal sealed class RadiusModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        double _radius = 16;
        public double Radius
        {
            get { return _radius; }
            set { if (_radius == value) return; _radius = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("Radius")); }
        }
    }

    /// <summary>Small bar: Radius slider (Affinity RangeSlider + UnitEditor) and Apply.</summary>
    internal static class RoundBar
    {
        public static Window Build(Action<double> apply)
        {
            var model = new RadiusModel();
            var win = CreateWindow();
            win.Title = "Round Corners";
            win.Width = 440;
            win.SizeToContent = SizeToContent.Height;
            win.ResizeMode = ResizeMode.NoResize;
            win.Topmost = true;
            win.Dispatcher.UnhandledException += (s, e) => { RoundCornersPlugin.Ctx?.LogError("Bar UI exception", e.Exception); e.Handled = true; };
            if (Application.Current.MainWindow != null && Application.Current.MainWindow != win)
            {
                win.Owner = Application.Current.MainWindow;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            var grid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = "Radius:", VerticalAlignment = VerticalAlignment.Center };
            var slider = Ui.NumberSlider(model, "Radius", 0, 500, 0, "Pixel");
            Grid.SetColumn(slider, 1);
            grid.Children.Add(label); grid.Children.Add(slider);

            var btn = new Button { Content = "Apply", Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(0, 5, 0, 5) };
            Grid.SetRow(btn, 1); Grid.SetColumnSpan(btn, 2);
            btn.Click += (s, e) => { var r = model.Radius; win.Close(); win.Dispatcher.BeginInvoke(new Action(() => apply(r))); };
            grid.Children.Add(btn);
            win.Content = grid;
            return win;
        }

        static Window CreateWindow()
        {
            try { return (Window)Activator.CreateInstance(RoundCornersPlugin.Asm("Serif.Affinity").GetType("Serif.Affinity.UI.AffinityDialog", true)); }
            catch { return new Window(); }
        }
    }

    /// <summary>Affinity RangeSlider + UnitEditor with WPF fallback.</summary>
    internal static class Ui
    {
        public static FrameworkElement NumberSlider(object model, string prop, double min, double max, int precision, string unit)
        {
            var binding = new Binding(prop) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
            try
            {
                var aff = RoundCornersPlugin.Asm("Serif.Affinity");
                var range = aff.GetType("Serif.Affinity.UI.Controls.RangeSlider", true);
                var editorT = aff.GetType("Serif.Affinity.UI.Units.UnitEditor", true);
                var uv = RoundCornersPlugin.PType("Serif.Interop.Persona.Units.UnitValue");
                var ut = RoundCornersPlugin.PType("Serif.Interop.Persona.Units.UnitType");
                var u = Enum.Parse(ut, unit);

                var slider = (Slider)Activator.CreateInstance(range);
                slider.Minimum = min; slider.Maximum = max;
                slider.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, binding);

                var editor = (Control)Activator.CreateInstance(editorT);
                var pdp = editorT.GetField("PrecisionProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) as DependencyProperty;
                if (pdp != null) editor.SetValue(pdp, (int?)precision);
                editorT.GetProperty("OutputUnitType")?.SetValue(editor, u);
                editor.Width = 72; editor.Margin = new Thickness(12, 0, 0, 0);
                var dp = (DependencyProperty)editorT.GetField("UnitValueProperty", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                BindingOperations.SetBinding(editor, dp, new Binding(prop)
                {
                    Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    Converter = new DoubleToUnitValue(uv, u)
                });

                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(editor, 1);
                g.Children.Add(slider); g.Children.Add(editor);
                return g;
            }
            catch (Exception ex)
            {
                RoundCornersPlugin.Ctx?.LogWarning("Serif slider failed, using WPF: " + ex.Message);
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                var sl = new Slider { Minimum = min, Maximum = max };
                sl.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, binding);
                var box = new TextBox { Margin = new Thickness(6, 0, 0, 0) };
                box.SetBinding(TextBox.TextProperty, new Binding(prop) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, StringFormat = "F0" });
                Grid.SetColumn(box, 1);
                g.Children.Add(sl); g.Children.Add(box);
                return g;
            }
        }
    }

    internal sealed class DoubleToUnitValue : IValueConverter
    {
        readonly ConstructorInfo _ctor; readonly PropertyInfo _value; readonly Type _type; readonly object _unit;
        public DoubleToUnitValue(Type unitValue, object unit)
        {
            _type = unitValue; _unit = unit;
            _ctor = unitValue.GetConstructor(new[] { typeof(double), unit.GetType() });
            _value = unitValue.GetProperty("Value");
        }
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => v is double d ? _ctor.Invoke(new object[] { d, _unit }) : null;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => v != null && _type.IsInstanceOfType(v) ? _value.GetValue(v) : 0.0;
    }
}
