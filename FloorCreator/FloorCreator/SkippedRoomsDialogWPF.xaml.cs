using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace FloorCreator
{
    public partial class SkippedRoomsDialogWPF : Window
    {
        ExternalCommandData CommandData;
        public SkippedRoomsDialogWPF(ExternalCommandData commandData, List<Room> skippedRoomsList)
        {
            CommandData = commandData;
            InitializeComponent();
            listBox_SkippedRooms.ItemsSource = skippedRoomsList;
            listBox_SkippedRooms.DisplayMemberPath = "Name";
        }

        private void btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
        private void SkippedRoomsDialogWPF_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                DialogResult = true;
                Close();
            }

            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        private void listBox_SkippedRooms_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            using (Transaction transaction = new Transaction(CommandData.Application.ActiveUIDocument.Document))
            {
                Room selectedRoom = listBox_SkippedRooms.SelectedItem as Room;
                View3D view3D = new FilteredElementCollector(CommandData.Application.ActiveUIDocument.Document)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => v.Name == "FloorCreator3DView");
                
                if(view3D == null)
                {
                    transaction.Start("Фокус на помещении");
                    ViewFamilyType viewFamilyType = new FilteredElementCollector(CommandData.Application.ActiveUIDocument.Document)
                        .OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>()
                        .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                    view3D = View3D.CreateIsometric(CommandData.Application.ActiveUIDocument.Document, viewFamilyType.Id);
                    view3D.Name = "FloorCreator3DView";
                    view3D.SetSectionBox(selectedRoom.get_BoundingBox(view3D));
                    transaction.Commit();
                    CommandData.Application.ActiveUIDocument.ActiveView = view3D;
                    List<ElementId> elementIds = new List<ElementId>();
                    elementIds.Add(selectedRoom.Id);
                    CommandData.Application.ActiveUIDocument.Selection.SetElementIds(elementIds);
                }
                else
                {
                    transaction.Start("Фокус на помещении");
                    if (view3D.IsSectionBoxActive == false)
                    {
                        view3D.IsSectionBoxActive = true;
                    }
                    view3D.SetSectionBox(selectedRoom.get_BoundingBox(view3D));
                    transaction.Commit();
                    CommandData.Application.ActiveUIDocument.ActiveView = view3D;
                    List<ElementId> elementIds = new List<ElementId>();
                    elementIds.Add(selectedRoom.Id);
                    CommandData.Application.ActiveUIDocument.Selection.SetElementIds(elementIds);
                }

            }
        }
    }
}
