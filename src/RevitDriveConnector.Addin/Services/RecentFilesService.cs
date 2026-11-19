using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitDriveConnector.Addin.Services
{
 public static class RecentFilesService
 {
 public static void RegisterInRevitRecentFiles(string revitVersion, string localFilePath)
 {
 try
 {
 if (string.IsNullOrEmpty(localFilePath) || !File.Exists(localFilePath)) return;

 string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
 string iniPath = Path.Combine(appData, "Autodesk", "Revit", $"Autodesk Revit {revitVersion}", "Revit.ini");

 if (!File.Exists(iniPath)) return;

 var lines = File.ReadAllLines(iniPath).ToList();
 int recentSectionIndex = lines.FindIndex(l => l.Trim().Equals("[Recent File List]", StringComparison.OrdinalIgnoreCase));

 if (recentSectionIndex == -1)
 {
 lines.Add("[Recent File List]");
 lines.Add($"File1={localFilePath}");
 File.WriteAllLines(iniPath, lines);
 return;
 }

 // Leggiamo i file recenti esistenti
 var recentFiles = new List<string> { localFilePath };
 int scanIndex = recentSectionIndex + 1;
 var linesToRemove = new List<int>();

 string projectName = Path.GetFileNameWithoutExtension(localFilePath);
 // Rimuoviamo eventuali suffissi per fare match pulito
 if (projectName.Contains("_"))
 {
 projectName = projectName.Substring(0, projectName.IndexOf('_'));
 }

 while (scanIndex < lines.Count && !lines[scanIndex].Trim().StartsWith("["))
 {
 string line = lines[scanIndex].Trim();
 if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase) && line.Contains("="))
 {
 string val = line.Substring(line.IndexOf('=') + 1).Trim();
 // Non reinseriamo lo stesso file o vecchi cloni temporanei dello stesso progetto
 if (!string.Equals(val, localFilePath, StringComparison.OrdinalIgnoreCase) &&
 !val.Contains($"{projectName}_Central", StringComparison.OrdinalIgnoreCase) &&
 File.Exists(val))
 {
 if (!recentFiles.Contains(val, StringComparer.OrdinalIgnoreCase))
 {
 recentFiles.Add(val);
 }
 }
 linesToRemove.Add(scanIndex);
 }
 scanIndex++;
 }

 // Rimuoviamo le vecchie righe dei file recenti a partire dall'indice più alto
 linesToRemove.Reverse();
 foreach (int idx in linesToRemove)
 {
 lines.RemoveAt(idx);
 }

 // Inseriamo il nuovo elenco numerato (max 16 file)
 int insertPos = recentSectionIndex + 1;
 int count = Math.Min(recentFiles.Count, 16);
 for (int i = 0; i < count; i++)
 {
 lines.Insert(insertPos + i, $"File{i + 1}={recentFiles[i]}");
 }

 File.WriteAllLines(iniPath, lines);
 Console.WriteLine($"[RecentFilesService] Registrato in prima posizione nella Homepage Revit: {localFilePath}");
 }
 catch (Exception ex)
 {
 Console.WriteLine($"[RecentFilesService] Avviso aggiornamento Revit.ini: {ex.Message}");
 }
 }
 }
}
