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

        public static FloorCreatorSettings GetSettings()
        {
            FloorCreatorSettings floorCreatorSettings = null;
            string assemblyPathAll = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string fileName = "FloorCreatorSettings.xml";
            string assemblyPath = assemblyPathAll.Replace("FloorCreator.dll", fileName);

            if (File.Exists(assemblyPath))
            {
                using (FileStream fs = new FileStream(assemblyPath, FileMode.Open))
                {
                    XmlSerializer xSer = new XmlSerializer(typeof(FloorCreatorSettings));
                    floorCreatorSettings = xSer.Deserialize(fs) as FloorCreatorSettings;
                    fs.Close();
                }
            }
            else
            {
                floorCreatorSettings = new FloorCreatorSettings();
            }

            return floorCreatorSettings;
        }

        public void SaveSettings()
        {
            string assemblyPathAll = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string fileName = "FloorCreatorSettings.xml";
            string assemblyPath = assemblyPathAll.Replace("FloorCreator.dll", fileName);

            if (File.Exists(assemblyPath))
            {
                File.Delete(assemblyPath);
            }

            using (FileStream fs = new FileStream(assemblyPath, FileMode.Create))
            {
                XmlSerializer xSer = new XmlSerializer(typeof(FloorCreatorSettings));
                xSer.Serialize(fs, this);
                fs.Close();
            }
        }
    }
}
