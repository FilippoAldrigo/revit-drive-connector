using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace RevitDriveConnector.Addin.UI
{
 public static class IconHelper
 {
 public static BitmapImage? GetBitmap(string iconFileName)
 {
 try
 {
 var assembly = Assembly.GetExecutingAssembly();
 string resourceName = $"RevitDriveConnector.Addin.Resources.Icons.{iconFileName}";
 using Stream? stream = assembly.GetManifestResourceStream(resourceName);
 if (stream == null)
 {
 return null;
 }

 var bitmap = new BitmapImage();
 bitmap.BeginInit();
 bitmap.StreamSource = stream;
 bitmap.CacheOption = BitmapCacheOption.OnLoad;
 bitmap.EndInit();
 bitmap.Freeze();
 return bitmap;
 }
 catch
 {
 return null;
 }
 }

 public static void ApplyWindowIcon(Window window)
 {
 try
 {
 var icon = GetBitmap("app_icon_32.png");
 if (icon != null)
 {
 window.Icon = icon;
 }
 }
 catch { }
 }
 }
}
