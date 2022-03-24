using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace FloorCreator
{
    /// <summary>
    /// Логика взаимодействия для FloorCreatorWPF.xaml
    /// </summary>
    public partial class FloorCreatorWPF : Window
    {
        public string FloorCreationOptionSelectedName;
        public string InRoomsSelectedName;
        public FloorType SelectedFloorType;

        FloorCreatorSettings FloorCreatorSettingsParam = null;

        public FloorCreatorWPF(List<FloorType> floorTypesList)
        {
            InitializeComponent();
            rbt_ManualCreation.IsChecked = true;

            comboBox_FloorType.ItemsSource = floorTypesList;
            comboBox_FloorType.DisplayMemberPath = "Name";

            FloorCreatorSettingsParam = FloorCreatorSettings.GetSettings();
            if(FloorCreatorSettingsParam.FloorTapeName != null)
            {
                comboBox_FloorType.SelectedItem = floorTypesList
                    .FirstOrDefault(ft => ft.Name == FloorCreatorSettingsParam.FloorTapeName);
            }
            else
            {
                if (floorTypesList.Count != 0)
                {
                    comboBox_FloorType.SelectedItem = comboBox_FloorType.Items.GetItemAt(0);
                }
            }
        }

        //Изменение опции создания полов
        private void groupBox_FloorCreationOptionCheckedRB(object sender, RoutedEventArgs e)
        {
            string actionSelectionButtonName = (groupBox_FloorCreationOption.Content as System.Windows.Controls.Grid)
                .Children.OfType<RadioButton>()
                .FirstOrDefault(rb => rb.IsChecked.Value == true)
                .Name;
            if (actionSelectionButtonName == "rbt_ManualCreation")
            {
                groupBox_InRooms.IsEnabled = false;
                comboBox_FloorType.IsEnabled = true;
            }
            else if (actionSelectionButtonName == "rbt_CreateFromParameter")
            {
                comboBox_FloorType.IsEnabled = false;
                groupBox_InRooms.IsEnabled = true;
            }
        }

        private void btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            GetFormSelectionResult();
            SaveSettings();
            DialogResult = true;
            Close();
        }

        private void btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
        private void FloorCreatorWPF_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                GetFormSelectionResult();
                SaveSettings();
                DialogResult = true;
                Close();
            }

            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        private void GetFormSelectionResult()
        {
            FloorCreationOptionSelectedName = (groupBox_FloorCreationOption.Content as System.Windows.Controls.Grid)
                .Children.OfType<RadioButton>()
                .FirstOrDefault(rb => rb.IsChecked.Value == true)
                .Name;
            InRoomsSelectedName = (groupBox_InRooms.Content as System.Windows.Controls.Grid)
                .Children.OfType<RadioButton>()
                .FirstOrDefault(rb => rb.IsChecked.Value == true)
                .Name;
            SelectedFloorType = comboBox_FloorType.SelectedItem as FloorType;
        }

        private void SaveSettings()
        {
            FloorCreatorSettingsParam.FloorTapeName = SelectedFloorType.Name;
            FloorCreatorSettingsParam.SaveSettings();
        }
    }
}
