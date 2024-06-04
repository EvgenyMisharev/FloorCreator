using Autodesk.Revit.DB.Architecture;
using Microsoft.Win32;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace FloorCreator
{
    public partial class ErrorRoomsDialogWPF : Window
    {
        private List<Room> _errorRooms;

        public ErrorRoomsDialogWPF(List<Room> errorRooms)
        {
            InitializeComponent();
            _errorRooms = errorRooms;
            List<string> roomDescriptions = new List<string>();
            foreach (Room room in errorRooms)
            {
                roomDescriptions.Add($"ID: {room.Id.IntegerValue}, Номер: {room.Number}, Имя: {room.Name}");
            }
            lstErrorRooms.ItemsSource = roomDescriptions;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                FileName = "ErrorRooms.txt",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                using (StreamWriter writer = new StreamWriter(saveFileDialog.FileName))
                {
                    writer.WriteLine("Не удалось создать полы в следующих помещениях:");
                    foreach (Room room in _errorRooms)
                    {
                        writer.WriteLine($"ID: {room.Id.IntegerValue}, Номер: {room.Number}, Имя: {room.Name}");
                    }
                }
                MessageBox.Show($"Список помещений сохранен в {saveFileDialog.FileName}", "Сохранение завершено", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
