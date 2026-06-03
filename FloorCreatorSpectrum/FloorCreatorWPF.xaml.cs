using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Grid = System.Windows.Controls.Grid;

namespace FloorCreator
{
    public partial class FloorCreatorWPF : Window
    {
        public string FloorCreationOptionSelectedName;
        public string InRoomsSelectedName;
        public FloorType SelectedFloorType;
        public double FloorLevelOffset;

        public bool FillDoorPatches => chk_FillDoorPatches.IsChecked == true;
        public bool FilterDoorsByPhase { get; private set; }
        public ElementId SelectedDoorPhaseFilterId { get; private set; } = ElementId.InvalidElementId;

        FloorCreatorSettings FloorCreatorSettingsItem = null;
        private readonly List<PhaseFilterSelectionItem> _phaseFilterItems;

        public FloorCreatorWPF(System.Collections.Generic.List<FloorType> floorTypesList)
            : this(
                floorTypesList,
                new List<PhaseFilterSelectionItem>(),
                ElementId.InvalidElementId)
        {
        }

        public FloorCreatorWPF(
            System.Collections.Generic.List<FloorType> floorTypesList,
            IEnumerable<PhaseFilterSelectionItem> phaseFilterItems,
            ElementId defaultPhaseFilterId)
        {
            _phaseFilterItems = phaseFilterItems?.ToList() ?? new List<PhaseFilterSelectionItem>();

            InitializeComponent();

            comboBox_FloorType.ItemsSource = floorTypesList;
            comboBox_FloorType.DisplayMemberPath = "Name";
            comboBox_DoorPhaseFilter.ItemsSource = _phaseFilterItems;
            comboBox_DoorPhaseFilter.DisplayMemberPath = nameof(PhaseFilterSelectionItem.Name);

            comboBox_DoorPhaseFilter.SelectedItem = _phaseFilterItems
                .FirstOrDefault(pf => ElementIdCompat.HasSameValue(pf.PhaseFilterId, defaultPhaseFilterId))
                ?? _phaseFilterItems.FirstOrDefault();

            // Базовые дефолты (если файла нет/битый/пустой)
            rbt_ManualCreation.IsChecked = true;
            rbt_InSelected.IsChecked = true;

            if (comboBox_FloorType.Items.Count > 0)
                comboBox_FloorType.SelectedItem = comboBox_FloorType.Items[0];

            textBox_FloorLevelOffset.Text = "0";
            chk_FillDoorPatches.IsChecked = false;
            chk_FilterDoorsByPhase.IsChecked = false;

            // Новая схема: GetSettings() почти всегда возвращает объект.
            FloorCreatorSettingsItem = FloorCreatorSettings.GetSettings();

            // Применяем настройки только если они реально содержательные
            bool hasAnySavedValue =
                FloorCreatorSettingsItem != null &&
                (!string.IsNullOrWhiteSpace(FloorCreatorSettingsItem.FloorCreationOptionSelectedName) ||
                 !string.IsNullOrWhiteSpace(FloorCreatorSettingsItem.InRoomsSelectedName) ||
                 !string.IsNullOrWhiteSpace(FloorCreatorSettingsItem.FloorTypeName) ||
                 !string.IsNullOrWhiteSpace(FloorCreatorSettingsItem.FloorLevelOffset) ||
                 FloorCreatorSettingsItem.FillDoorPatches ||
                 FloorCreatorSettingsItem.FilterDoorsByPhase);

            if (hasAnySavedValue)
            {
                // Опция создания
                if (FloorCreatorSettingsItem.FloorCreationOptionSelectedName == "rbt_CreateFromParameter")
                    rbt_CreateFromParameter.IsChecked = true;
                else
                    rbt_ManualCreation.IsChecked = true;

                // В помещениях
                if (FloorCreatorSettingsItem.InRoomsSelectedName == "rbt_InWholeProject")
                    rbt_InWholeProject.IsChecked = true;
                else
                    rbt_InSelected.IsChecked = true;

                // Тип пола
                var savedFloorType = floorTypesList.FirstOrDefault(ct => ct.Name == FloorCreatorSettingsItem.FloorTypeName);
                if (savedFloorType != null)
                    comboBox_FloorType.SelectedItem = savedFloorType;

                // Смещение (если пусто — 0)
                textBox_FloorLevelOffset.Text =
                    !string.IsNullOrWhiteSpace(FloorCreatorSettingsItem.FloorLevelOffset)
                        ? FloorCreatorSettingsItem.FloorLevelOffset
                        : "0";

                chk_FillDoorPatches.IsChecked = FloorCreatorSettingsItem.FillDoorPatches;
                chk_FilterDoorsByPhase.IsChecked = FloorCreatorSettingsItem.FilterDoorsByPhase;
            }

            // Обновляем доступность контролов после выставления радио
            groupBox_FloorCreationOption_Checked(null, null);
            UpdateDoorPhaseSelectorState();
        }

        //Изменение опции создания полов
        private void groupBox_FloorCreationOption_Checked(object sender, RoutedEventArgs e)
        {
            if (groupBox_FloorCreationOption == null)
                return;

            var host = groupBox_FloorCreationOption.Content as StackPanel;
            if (host == null)
                return;

            var checkedRb = host.Children
                .OfType<RadioButton>()
                .FirstOrDefault(rb => rb.IsChecked == true);

            string actionSelectionButtonName = checkedRb != null ? checkedRb.Name : "rbt_ManualCreation";

            if (actionSelectionButtonName == "rbt_ManualCreation")
            {
                if (groupBox_InRooms != null) groupBox_InRooms.IsEnabled = false;
                if (comboBox_FloorType != null) comboBox_FloorType.IsEnabled = true;
            }
            else if (actionSelectionButtonName == "rbt_CreateFromParameter")
            {
                if (groupBox_InRooms != null) groupBox_InRooms.IsEnabled = true;
                if (comboBox_FloorType != null) comboBox_FloorType.IsEnabled = false;
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
                btn_Ok_Click(sender, e);
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

            // Опция создания пола (верхняя группа)
            FloorCreationOptionSelectedName = GetCheckedRadioButtonName(groupBox_FloorCreationOption, "rbt_ManualCreation");
            FloorCreatorSettingsItem.FloorCreationOptionSelectedName = FloorCreationOptionSelectedName;

            // В помещениях (StackPanel или Grid)
            InRoomsSelectedName = GetCheckedRadioButtonName(groupBox_InRooms, "rbt_InSelected");
            FloorCreatorSettingsItem.InRoomsSelectedName = InRoomsSelectedName;

            // Тип пола
            SelectedFloorType = comboBox_FloorType?.SelectedItem as FloorType;
            FloorCreatorSettingsItem.FloorTypeName = SelectedFloorType?.Name;

            // Смещение: принимаем и "," и ".", сохраняем нормализованно
            string raw = textBox_FloorLevelOffset?.Text;
            string normalizedText = NormalizeOffsetText(raw, out double offsetValue);

            FloorLevelOffset = offsetValue;
            if (textBox_FloorLevelOffset != null)
                textBox_FloorLevelOffset.Text = normalizedText;

            FloorCreatorSettingsItem.FloorLevelOffset = normalizedText;

            // Чекбокс
            FloorCreatorSettingsItem.FillDoorPatches = chk_FillDoorPatches?.IsChecked == true;
            FilterDoorsByPhase =
                FloorCreatorSettingsItem.FillDoorPatches &&
                chk_FilterDoorsByPhase?.IsChecked == true &&
                comboBox_DoorPhaseFilter.SelectedItem is PhaseFilterSelectionItem;

            if (FilterDoorsByPhase)
            {
                var selectedPhaseFilter = comboBox_DoorPhaseFilter.SelectedItem as PhaseFilterSelectionItem;
                SelectedDoorPhaseFilterId = selectedPhaseFilter?.PhaseFilterId ?? ElementId.InvalidElementId;
            }
            else
            {
                SelectedDoorPhaseFilterId = ElementId.InvalidElementId;
            }

            FloorCreatorSettingsItem.FilterDoorsByPhase = FilterDoorsByPhase;

            FloorCreatorSettingsItem.SaveSettings();
        }

        private void CheckBox_DoorPatch_StateChanged(object sender, RoutedEventArgs e)
        {
            UpdateDoorPhaseSelectorState();
        }

        private void CheckBox_DoorPhase_StateChanged(object sender, RoutedEventArgs e)
        {
            UpdateDoorPhaseSelectorState();
        }

        private void UpdateDoorPhaseSelectorState()
        {
            if (chk_FillDoorPatches == null || chk_FilterDoorsByPhase == null || comboBox_DoorPhaseFilter == null)
                return;

            bool canUsePhase = chk_FillDoorPatches.IsChecked == true && _phaseFilterItems.Any();
            chk_FilterDoorsByPhase.IsEnabled = canUsePhase;
            if (!canUsePhase)
            {
                chk_FilterDoorsByPhase.IsChecked = false;
            }

            bool isEnabled = canUsePhase && chk_FilterDoorsByPhase.IsChecked == true;
            comboBox_DoorPhaseFilter.IsEnabled = isEnabled && _phaseFilterItems.Any();
        }

        private static string NormalizeOffsetText(string input, out double value)
        {
            value = 0;

            if (string.IsNullOrWhiteSpace(input))
                return "0";

            // Разрешаем оба разделителя
            string s = input.Trim().Replace(',', '.');

            // Парсим инвариантно
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                value = 0;
                return "0";
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string GetCheckedRadioButtonName(GroupBox groupBox, string fallbackName)
        {
            if (groupBox == null || groupBox.Content == null)
                return fallbackName;

            // Вариант 1: StackPanel
            var stack = groupBox.Content as StackPanel;
            if (stack != null)
            {
                var rb = stack.Children
                    .OfType<RadioButton>()
                    .FirstOrDefault(x => x.IsChecked == true);

                return rb != null ? rb.Name : fallbackName;
            }

            // Вариант 2: Grid (старый вариант разметки)
            var grid = groupBox.Content as Grid;
            if (grid != null)
            {
                var rb = grid.Children
                    .OfType<RadioButton>()
                    .FirstOrDefault(x => x.IsChecked == true);

                return rb != null ? rb.Name : fallbackName;
            }

            return fallbackName;
        }
    }
}
