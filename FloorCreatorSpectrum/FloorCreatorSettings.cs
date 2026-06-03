using System;
using System.IO;
using System.Xml.Serialization;

namespace FloorCreator
{
    public class FloorCreatorSettings
    {
        public string FloorCreationOptionSelectedName { get; set; }
        public string InRoomsSelectedName { get; set; }
        public string FloorTypeName { get; set; }
        public string FloorLevelOffset { get; set; }
        public bool FillDoorPatches { get; set; }
        public bool FilterDoorsByPhase { get; set; }

        private const string FileName = "FloorCreatorSettings.xml";

        private static string SettingsDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Citrus BIM",
                "FloorCreator");

        private static string SettingsFilePath => Path.Combine(SettingsDirectory, FileName);

        public static FloorCreatorSettings GetSettings()
        {
            if (!File.Exists(SettingsFilePath))
                return new FloorCreatorSettings();

            try
            {
                using (var fs = new FileStream(SettingsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var xSer = new XmlSerializer(typeof(FloorCreatorSettings));
                    return xSer.Deserialize(fs) as FloorCreatorSettings
                           ?? new FloorCreatorSettings();
                }
            }
            catch
            {
                return new FloorCreatorSettings();
            }
        }

        public void SaveSettings()
        {
            Directory.CreateDirectory(SettingsDirectory);

            var tmpPath = SettingsFilePath + ".tmp";

            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }

            var xSer = new XmlSerializer(typeof(FloorCreatorSettings));
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                xSer.Serialize(fs, this);
            }

            TryReplaceOrMove(tmpPath, SettingsFilePath);
        }

        private static void TryReplaceOrMove(string tmpPath, string targetPath)
        {
            try
            {
                if (File.Exists(targetPath))
                    File.Replace(tmpPath, targetPath, destinationBackupFileName: null);
                else
                    File.Move(tmpPath, targetPath);
            }
            catch
            {
                try
                {
                    if (File.Exists(targetPath))
                        File.Delete(targetPath);

                    File.Move(tmpPath, targetPath);
                }
                catch
                {
                }
            }
        }
    }
}
