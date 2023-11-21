using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FloorCreator
{
    public partial class FloorCreatorWPF : Window
    {
        public string FloorCreationOptionSelectedName;
        public string InRoomsSelectedName;
        public FloorType SelectedFloorType;
        public double FloorLevelOffset;

        FloorCreatorSettings FloorCreatorSettingsItem = null;

        public FloorCreatorWPF(List<FloorType> floorTypesList)
        {
            FloorCreatorSettingsItem = FloorCreatorSettings.GetSettings();
            InitializeComponent();
            comboBox_FloorType.ItemsSource = floorTypesList;
            comboBox_FloorType.DisplayMemberPath = "Name";

            if (FloorCreatorSettingsItem != null)
            {
                if (FloorCreatorSettingsItem.FloorCreationOptionSelectedName == "rbt_ManualCreation")
                {
                    rbt_ManualCreation.IsChecked = true;
                }
                else
                {
                    rbt_CreateFromParameter.IsChecked = true;
                }
                groupBox_FloorCreationOption_Checked(null, null);

                if (FloorCreatorSettingsItem.InRoomsSelectedName == "rbt_InSelected")
                {
                    rbt_InSelected.IsChecked = true;
                }
                else
                {
                    rbt_InWholeProject.IsChecked = true;
                }

                if (floorTypesList.FirstOrDefault(ct => ct.Name == FloorCreatorSettingsItem.FloorTypeName) != null)
                {
                    comboBox_FloorType.SelectedItem = floorTypesList.FirstOrDefault(ct => ct.Name == FloorCreatorSettingsItem.FloorTypeName);
                }
                else
                {
                    comboBox_FloorType.SelectedItem = comboBox_FloorType.Items[0];
                }

                textBox_FloorLevelOffset.Text = FloorCreatorSettingsItem.FloorLevelOffset;

            }
            else
            {
                rbt_ManualCreation.IsChecked = true;
                rbt_InSelected.IsChecked = true;
                comboBox_FloorType.SelectedItem = comboBox_FloorType.Items[0];
                groupBox_FloorCreationOption_Checked(null, null);
            }
        }

        //Изменение опции создания полов
        private void groupBox_FloorCreationOption_Checked(object sender, RoutedEventArgs e)
        {
            if (rbt_CreateFromParameter != null)
            {
                string actionSelectionButtonName = (groupBox_FloorCreationOption.Content as StackPanel)
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
                    groupBox_InRooms.IsEnabled = true;
                    comboBox_FloorType.IsEnabled = false;
                }
            }
        }

        private void btn_Ok_Click(object sender, RoutedEventArgs e)
        {
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

        private void SaveSettings()
        {
            FloorCreatorSettingsItem = new FloorCreatorSettings();
            FloorCreationOptionSelectedName = (groupBox_FloorCreationOption.Content as StackPanel)
                .Children.OfType<RadioButton>()
                .FirstOrDefault(rb => rb.IsChecked.Value == true)
                .Name;
            FloorCreatorSettingsItem.FloorCreationOptionSelectedName = FloorCreationOptionSelectedName;

            InRoomsSelectedName = (groupBox_InRooms.Content as System.Windows.Controls.Grid)
                .Children.OfType<RadioButton>()
                .FirstOrDefault(rb => rb.IsChecked.Value == true)
                .Name;
            FloorCreatorSettingsItem.InRoomsSelectedName = InRoomsSelectedName;

            SelectedFloorType = comboBox_FloorType.SelectedItem as FloorType;
            FloorCreatorSettingsItem.FloorTypeName = SelectedFloorType.Name;

            double.TryParse(textBox_FloorLevelOffset.Text, out FloorLevelOffset);
            FloorCreatorSettingsItem.FloorLevelOffset = textBox_FloorLevelOffset.Text;

            FloorCreatorSettingsItem.SaveSettings();
        }
    }
}
