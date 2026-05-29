using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FloorCreator
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    class FloorCreatorCommand : IExternalCommand
    {
        FloorCreatorProgressBarWPF floorCreatorProgressBarWPF;
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                _ = GetPluginStartInfo();
            }
            catch { }


            Document doc = commandData.Application.ActiveUIDocument.Document;
            Selection sel = commandData.Application.ActiveUIDocument.Selection;
            double shortCurveTolerance = commandData.Application.Application.ShortCurveTolerance;
            double curveCreationTolerance = shortCurveTolerance * 1.05;

            // Типы полов для формы
            List<FloorType> floorTypesList;

#if R2019 || R2020 || R2021 || R2022 || R2023 || R2024 || R2025

            floorTypesList = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .Where(ft => ft.Category != null && ft.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Floors)
                .Where(ft =>
                {
                    var p = ft.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL);
                    var s = p != null ? p.AsString() : null;
                    return s == "Пол" || s == "Полы";
                })
                .OrderBy(ft => ft.Name, new AlphanumComparatorFastString())
                .ToList();
#else

            floorTypesList = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .WhereElementIsElementType()
                .OfCategory(BuiltInCategory.OST_Floors)
                .Cast<FloorType>()
                .Where(f =>
                {
                    var p = f.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL);
                    var s = p != null ? p.AsString() : null;
                    return s == "Пол" || s == "Полы";
                })
                .OrderBy(f => f.Name, new AlphanumComparatorFastString())
                .ToList();

#endif

            if (floorTypesList.Count == 0)
            {
                TaskDialog.Show("Revit", "В проекте отсутствуют подготовленные типы полов! Обратитесь к инструкции через F1!");
                return Result.Cancelled;
            }

            //Вызов формы
            FloorCreatorWPF floorCreatorWPF = new FloorCreatorWPF(floorTypesList);
            floorCreatorWPF.ShowDialog();
            if (floorCreatorWPF.DialogResult != true)
            {
                return Result.Cancelled;
            }

            string floorCreationOptionSelectedName = floorCreatorWPF.FloorCreationOptionSelectedName;
            string inRoomsSelectedName = floorCreatorWPF.InRoomsSelectedName;
            FloorType selectedFloorType = floorCreatorWPF.SelectedFloorType;
            double floorLevelOffset = floorCreatorWPF.FloorLevelOffset / 304.8;

            bool needFillDoorPatches = floorCreatorWPF.FillDoorPatches;
            bool deleteOldFloors = floorCreatorWPF.DeleteOldFloors;

            List<Room> errorRooms = new List<Room>();

            //Ручное создание полов
            if (floorCreationOptionSelectedName == "rbt_ManualCreation")
            {
                List<Room> roomList = new List<Room>();
                roomList = GetRoomsFromCurrentSelection(doc, sel);
                if (roomList.Count == 0)
                {
                    RoomSelectionFilter selFilter = new RoomSelectionFilter();
                    IList<Reference> selRooms = null;
                    try
                    {
                        selRooms = sel.PickObjects(ObjectType.Element, selFilter, "Выберите помещения!");
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }

                    foreach (Reference roomRef in selRooms)
                    {
                        roomList.Add(doc.GetElement(roomRef) as Room);
                    }
                }

                using (TransactionGroup transGroup = new TransactionGroup(doc))
                {
                    using (Transaction t = new Transaction(doc))
                    {
                        transGroup.Start("Создание пола");

                        Thread newWindowThread = new Thread(new ThreadStart(ThreadStartingPoint));
                        newWindowThread.SetApartmentState(ApartmentState.STA);
                        newWindowThread.IsBackground = true;
                        newWindowThread.Start();
                        int step = 0;
                        Thread.Sleep(100);
                        floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Minimum = 0);
                        floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Maximum = roomList.Count);

                        foreach (Room room in roomList)
                        {
                            step++;
                            floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Value = step);
                            Level roomLevel = room.Level;
                            if (roomLevel == null)
                            {
                                continue;
                            }

                            double minLength = shortCurveTolerance;
                            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                            CurveArray firstRoomCurves = GetFilteredRoomCurves(loops, minLength, curveCreationTolerance);
                            if (firstRoomCurves.Size < 3)
                            {
                                errorRooms.Add(room);
                                continue;
                            }
                            List<Curve> mainEdgs = firstRoomCurves.Cast<Curve>().ToList();

                            if (needFillDoorPatches)
                            {
                                firstRoomCurves = ApplyDoorPatchesToRoomCurves(doc, room, loops, firstRoomCurves, shortCurveTolerance, curveCreationTolerance);
                                if (firstRoomCurves.Size < 3)
                                {
                                    errorRooms.Add(room);
                                    continue;
                                }
                            }

                            CurveArray secondRoomCurves = new CurveArray();

                            for (int i = 0; i < loops.Count(); i++)
                            {
                                if (i == 0)
                                {
                                    //Пропускаем
                                }
                                else
                                {
                                    foreach (BoundarySegment seg in loops[i])
                                    {
                                        secondRoomCurves.Append(seg.GetCurve());
                                    }
                                }
                            }

                            if (deleteOldFloors)
                            {
#if R2019 || R2020 || R2021 || R2022 || R2023 || R2024 || R2025

                            List<Floor> floorList = new FilteredElementCollector(doc)
                                .OfClass(typeof(Floor))
                                .Cast<Floor>()
                                .Where(f => f.LevelId == room.LevelId)
                                .Where(f => f.Category != null && f.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Floors)
                                .Where(f =>
                                {
                                    var ft = f.FloorType;
                                    if (ft == null) return false;

                                    var p = ft.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL);
                                    var s = p != null ? p.AsString() : null;

                                    return s == "Пол" || s == "Полы";
                                })
                                .OrderBy(f => f.Name)
                                .ToList();

#else

                            List<Floor> floorList = new FilteredElementCollector(doc)
                                .OfClass(typeof(Floor))
                                .OfCategory(BuiltInCategory.OST_Floors)
                                .Cast<Floor>()
                                .Where(f => f.LevelId == room.LevelId)
                                .Where(f =>
                                {
                                    var ft = f.FloorType;
                                    if (ft == null) return false;

                                    var p = ft.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL);
                                    var s = p != null ? p.AsString() : null;

                                    return s == "Пол" || s == "Полы";
                                })
                                .OrderBy(f => f.Name)
                                .ToList();

#endif

                                t.Start("Удаление старого пола");
                                //Солид помещения
                                Solid roomSolid = null;
                                GeometryElement geomRoomElement = room.get_Geometry(new Options());
                                foreach (GeometryObject geomObj in geomRoomElement)
                                {
                                    roomSolid = geomObj as Solid;
                                    if (roomSolid != null) break;
                                }
                                foreach (Floor f in floorList)
                                {
                                    //Солид пола
                                    Solid floorSolid = null;
                                    GeometryElement geomFloorElement = f.get_Geometry(new Options());
                                    foreach (GeometryObject geomObj in geomFloorElement)
                                    {
                                        floorSolid = geomObj as Solid;
                                        if (floorSolid != null) break;
                                    }
                                    //Подъем пола на 500
                                    floorSolid = SolidUtils.CreateTransformed(floorSolid, Transform.CreateTranslation(new XYZ(0, 0, 500 / 304.8)));

                                    //Поиск пересечения между полом и помещением
                                    try
                                    {
                                        Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(floorSolid, roomSolid, BooleanOperationsType.Intersect);
                                        if (intersection != null)
                                        {
                                            double volumeOfIntersection = intersection.Volume;
                                            if (volumeOfIntersection != 0)
                                            {
                                                doc.Delete(f.Id);
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        //Пропуск
                                    }
                                }
                                t.Commit();
                            }

                            if (!TryPrepareFloorProfile(firstRoomCurves, shortCurveTolerance, curveCreationTolerance, out List<Curve> preparedProfileCurves))
                            {
                                errorRooms.Add(room);
                                continue;
                            }

                            //Создание нового пола
                            t.Start("Создание плиты");

                            Floor floor = null;
                            try
                            {
#if R2019 || R2020 || R2021 || R2022
                                floor = doc.Create.NewFloor(ToCurveArray(preparedProfileCurves), selectedFloorType, roomLevel, false);
                                floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(floorLevelOffset);
#else
                                if (!TryCreateValidatedCurveLoops(preparedProfileCurves, shortCurveTolerance, curveCreationTolerance, out List<CurveLoop> curveLoopList))
                                {
                                    errorRooms.Add(room);
                                    t.Commit();
                                    continue;
                                }

                                floor = Floor.Create(doc, curveLoopList, selectedFloorType.Id, roomLevel.Id);
                                floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(floorLevelOffset);
#endif
                            }
                            catch(Exception ex)
                            {
                                errorRooms.Add(room);
                                t.Commit();
                                continue;
                            }

                            //Удаление предупреждения о редактировании группы вне редактора
                            FailureHandlingOptions failureHandlingOptions = t.GetFailureHandlingOptions();
                            failureHandlingOptions.SetFailuresPreprocessor(new FloorIntersectionWarningSwallower());
                            t.SetFailureHandlingOptions(failureHandlingOptions);

                            t.Commit();
                            t.Start("Вырезание проемов");
                            if (secondRoomCurves.Size != 0)
                            {
                                try
                                {
                                    doc.Create.NewOpening(floor, secondRoomCurves, true);
                                }
                                catch
                                {

                                }
                            }
                            t.Commit();

                            //Полы в дверные проемы
                        }
                        floorCreatorProgressBarWPF.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.Close());
                        transGroup.Assimilate();
                    }
                }
            }
            else if (floorCreationOptionSelectedName == "rbt_CreateFromParameter")
            {
                if (inRoomsSelectedName == "rbt_InSelected")
                {
                    List<Room> roomList = new List<Room>();
                    roomList = GetRoomsFromCurrentSelection(doc, sel);
                    if (roomList.Count == 0)
                    {
                        RoomSelectionFilter selFilter = new RoomSelectionFilter();
                        IList<Reference> selRooms = null;
                        try
                        {
                            selRooms = sel.PickObjects(ObjectType.Element, selFilter, "Выберите помещения!");
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            return Result.Cancelled;
                        }

                        foreach (Reference roomRef in selRooms)
                        {
                            roomList.Add(doc.GetElement(roomRef) as Room);
                        }
                    }
                    //List<Room> skippedRoomsList = new List<Room>();
                    using (TransactionGroup transGroup = new TransactionGroup(doc))
                    {
                        using (Transaction t = new Transaction(doc))
                        {
                            transGroup.Start("Создание пола");

                            Thread newWindowThread = new Thread(new ThreadStart(ThreadStartingPoint));
                            newWindowThread.SetApartmentState(ApartmentState.STA);
                            newWindowThread.IsBackground = true;
                            newWindowThread.Start();
                            int step = 0;
                            Thread.Sleep(100);
                            floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Minimum = 0);
                            floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Maximum = roomList.Count);

                            foreach (Room room in roomList)
                            {
                                step++;
                                floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Value = step);

                                Level roomLevel = room.Level;
                                if (roomLevel == null)
                                {
                                    continue;
                                }

                            double minLength = shortCurveTolerance;
                                IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                                CurveArray firstRoomCurves = GetFilteredRoomCurves(loops, minLength, curveCreationTolerance);
                                if (firstRoomCurves.Size < 3)
                                {
                                    errorRooms.Add(room);
                                    continue;
                                }
                                if (needFillDoorPatches)
                                {
                                    firstRoomCurves = ApplyDoorPatchesToRoomCurves(doc, room, loops, firstRoomCurves, shortCurveTolerance, curveCreationTolerance);
                                    if (firstRoomCurves.Size < 3)
                                    {
                                        errorRooms.Add(room);
                                        continue;
                                    }
                                }
                                CurveArray secondRoomCurves = new CurveArray();
                                for (int i = 0; i < loops.Count(); i++)
                                {
                                    if (i == 0)
                                    {
                                        //Пропустить
                                    }
                                    else
                                    {
                                        foreach (BoundarySegment seg in loops[i])
                                        {
                                            secondRoomCurves.Append(seg.GetCurve());
                                        }
                                    }
                                }

                                FloorType typeFromParameter = floorTypesList
                                    .FirstOrDefault(ft => !string.IsNullOrEmpty(ft.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID).AsString()) &&
                                    ft.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID).AsString() == room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR).AsString());
                                if (typeFromParameter != null)
                                {
                                    if (deleteOldFloors)
                                    {
                                        // Удаление старого пола
#if R2019 || R2020 || R2021 || R2022 || R2023 || R2024 || R2025

                                        List<Floor> floorList = new FilteredElementCollector(doc)
                                            .OfClass(typeof(Floor))
                                            .Cast<Floor>()
                                            .Where(f => f.LevelId == room.LevelId)
                                            .Where(f => f.Category != null && f.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Floors)
                                            .Where(f =>
                                            {
                                                var ft = f.FloorType;
                                                if (ft == null) return false;

                                                var p = ft.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL);
                                                var s = p != null ? p.AsString() : null;

                                                return s == "Пол" || s == "Полы";
                                            })
                                            .OrderBy(f => f.Name)
                                            .ToList();

#else

                                        List<Floor> floorList = new FilteredElementCollector(doc)
                                            .OfClass(typeof(Floor))
                                            .OfCategory(BuiltInCategory.OST_Floors)
                                            .Cast<Floor>()
                                            .Where(f => f.LevelId == room.LevelId)
                                            .Where(f =>
                                            {
                                                var ft = f.FloorType;
                                                if (ft == null) return false;

                                                var p = ft.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL);
                                                var s = p != null ? p.AsString() : null;

                                                return s == "Пол" || s == "Полы";
                                            })
                                            .OrderBy(f => f.Name)
                                            .ToList();

#endif

                                        t.Start("Удаление старого пола");
                                        //Солид помещения
                                        Solid roomSolid = null;
                                        GeometryElement geomRoomElement = room.get_Geometry(new Options());
                                        foreach (GeometryObject geomObj in geomRoomElement)
                                        {
                                            roomSolid = geomObj as Solid;
                                            if (roomSolid != null) break;
                                        }
                                        foreach (Floor f in floorList)
                                        {
                                            //Солид пола
                                            Solid floorSolid = null;
                                            GeometryElement geomFloorElement = f.get_Geometry(new Options());
                                            foreach (GeometryObject geomObj in geomFloorElement)
                                            {
                                                floorSolid = geomObj as Solid;
                                                if (floorSolid != null) break;
                                            }
                                            //Подъем пола на 500
                                            floorSolid = SolidUtils.CreateTransformed(floorSolid, Transform.CreateTranslation(new XYZ(0, 0, 500 / 304.8)));

                                            //Поиск пересечения между полом и помещением
                                            try
                                            {
                                                Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(floorSolid, roomSolid, BooleanOperationsType.Intersect);
                                                if (intersection != null)
                                                {
                                                    double volumeOfIntersection = intersection.Volume;
                                                    if (volumeOfIntersection != 0)
                                                    {
                                                        doc.Delete(f.Id);
                                                    }
                                                }
                                            }
                                            catch
                                            {
                                                //Пропуск
                                            }

                                        }
                                        t.Commit();
                                    }

                                    if (!TryPrepareFloorProfile(firstRoomCurves, shortCurveTolerance, curveCreationTolerance, out List<Curve> preparedProfileCurves))
                                    {
                                        errorRooms.Add(room);
                                        continue;
                                    }

                                    t.Start("Создание плиты");
                                    Floor floor = null;
                                    try
                                    {
#if R2019 || R2020 || R2021 || R2022
                                        floor = doc.Create.NewFloor(ToCurveArray(preparedProfileCurves), typeFromParameter, roomLevel, false);
                                        floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(floorLevelOffset);
#else
                                        if (!TryCreateValidatedCurveLoops(preparedProfileCurves, shortCurveTolerance, curveCreationTolerance, out List<CurveLoop> curveLoopList))
                                        {
                                            errorRooms.Add(room);
                                            t.Commit();
                                            continue;
                                        }

                                        floor = Floor.Create(doc, curveLoopList, typeFromParameter.Id, roomLevel.Id);
                                        floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(floorLevelOffset);
#endif
                                    }
                                    catch
                                    {
                                        errorRooms.Add(room);
                                        t.Commit();
                                        continue;
                                    }

                                    //Удаление предупреждения о редактировании группы вне редактора
                                    FailureHandlingOptions failureHandlingOptions = t.GetFailureHandlingOptions();
                                    failureHandlingOptions.SetFailuresPreprocessor(new FloorIntersectionWarningSwallower());
                                    t.SetFailureHandlingOptions(failureHandlingOptions);
                                    //СОБРАТЬ ПРЕДУПРЕЖДЕНИЯ ПО ПОМЕЩЕНИЯМ!!!!
                                    t.Commit();

                                    t.Start("Вырезание проемов");
                                    if (secondRoomCurves.Size != 0)
                                    {
                                        try
                                        {
                                            doc.Create.NewOpening(floor, secondRoomCurves, true);
                                        }
                                        catch
                                        {

                                        }
                                    }
                                    t.Commit();
                                }
                                else
                                {
                                    //skippedRoomsList.Add(room);
                                }

                            }
                            floorCreatorProgressBarWPF.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.Close());
                            transGroup.Assimilate();
                        }
                    }
                }
                else if (inRoomsSelectedName == "rbt_InWholeProject")
                {
                    List<Room> roomList = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .OfClass(typeof(SpatialElement))
                        .WhereElementIsNotElementType()
                        .Cast<Room>()
                        .Where(r => Math.Round(r.Area, 6) != 0)
                        .ToList();

                    //List<Room> skippedRoomsList = new List<Room>();
                    using (TransactionGroup transGroup = new TransactionGroup(doc))
                    {
                        using (Transaction t = new Transaction(doc))
                        {
                            transGroup.Start("Создание пола");

                            Thread newWindowThread = new Thread(new ThreadStart(ThreadStartingPoint));
                            newWindowThread.SetApartmentState(ApartmentState.STA);
                            newWindowThread.IsBackground = true;
                            newWindowThread.Start();
                            int step = 0;
                            Thread.Sleep(100);
                            floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Minimum = 0);
                            floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Maximum = roomList.Count);

                            foreach (Room room in roomList)
                            {
                                step++;
                                floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.pb_FloorCreatorProgressBar.Value = step);

                                Level roomLevel = room.Level;
                                if (roomLevel == null)
                                {
                                    continue;
                                }

                                double minLength = shortCurveTolerance;
                                IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                                CurveArray firstRoomCurves = GetFilteredRoomCurves(loops, minLength, curveCreationTolerance);
                                if (firstRoomCurves.Size < 3)
                                {
                                    errorRooms.Add(room);
                                    continue;
                                }
                                if (needFillDoorPatches)
                                {
                                    firstRoomCurves = ApplyDoorPatchesToRoomCurves(doc, room, loops, firstRoomCurves, shortCurveTolerance, curveCreationTolerance);
                                    if (firstRoomCurves.Size < 3)
                                    {
                                        errorRooms.Add(room);
                                        continue;
                                    }
                                }
                                CurveArray secondRoomCurves = new CurveArray();

                                for (int i = 0; i < loops.Count(); i++)
                                {
                                    if (i == 0)
                                    {
                                        //Пропустить
                                    }
                                    else
                                    {
                                        foreach (BoundarySegment seg in loops[i])
                                        {
                                            secondRoomCurves.Append(seg.GetCurve());
                                        }
                                    }
                                }

                                FloorType typeFromParameter = floorTypesList
                                    .FirstOrDefault(ft => !string.IsNullOrEmpty(ft.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID).AsString()) &&
                                    ft.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID).AsString() == room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR).AsString());
                                if (typeFromParameter != null)
                                {
                                    if (deleteOldFloors)
                                    {
                                        //Удаление старого пола
#if R2019 || R2020 || R2021 || R2022 || R2023 || R2024 || R2025

                                        List<Floor> floorList = new FilteredElementCollector(doc)
                                            .OfClass(typeof(Floor))
                                            .Cast<Floor>()
                                            .Where(f => f.LevelId == room.LevelId)
                                            .Where(f => f.Category != null && f.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Floors)
                                            .Where(f =>
                                            {
                                                var ft = f.FloorType;
                                                if (ft == null) return false;

                                                var p = ft.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL);
                                                var s = p != null ? p.AsString() : null;

                                                return s == "Пол" || s == "Полы";
                                            })
                                            .OrderBy(f => f.Name)
                                            .ToList();

#else

                                        List<Floor> floorList = new FilteredElementCollector(doc)
                                            .OfCategory(BuiltInCategory.OST_Floors)
                                            .WhereElementIsNotElementType()
                                            .Cast<Floor>()
                                            .Where(f => f.LevelId == room.LevelId)
                                            .Where(f =>
                                            {
                                                var ft = f.FloorType;
                                                if (ft == null) return false;

                                                var p = ft.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL);
                                                var s = p != null ? p.AsString() : null;

                                                return s == "Пол" || s == "Полы";
                                            })
                                            .OrderBy(f => f.Name)
                                            .ToList();

#endif

                                        t.Start("Удаление старого пола");
                                        //Солид помещения
                                        Solid roomSolid = null;
                                        GeometryElement geomRoomElement = room.get_Geometry(new Options());
                                        foreach (GeometryObject geomObj in geomRoomElement)
                                        {
                                            roomSolid = geomObj as Solid;
                                            if (roomSolid != null) break;
                                        }
                                        foreach (Floor f in floorList)
                                        {
                                            //Солид пола
                                            Solid floorSolid = null;
                                            GeometryElement geomFloorElement = f.get_Geometry(new Options());
                                            foreach (GeometryObject geomObj in geomFloorElement)
                                            {
                                                floorSolid = geomObj as Solid;
                                                if (floorSolid != null) break;
                                            }
                                            //Подъем пола на 500
                                            floorSolid = SolidUtils.CreateTransformed(floorSolid, Transform.CreateTranslation(new XYZ(0, 0, 500 / 304.8)));

                                            //Поиск пересечения между полом и помещением
                                            try
                                            {
                                                Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(floorSolid, roomSolid, BooleanOperationsType.Intersect);
                                                if (intersection != null)
                                                {
                                                    double volumeOfIntersection = intersection.Volume;
                                                    if (volumeOfIntersection != 0)
                                                    {
                                                        doc.Delete(f.Id);
                                                    }
                                                }
                                            }
                                            catch
                                            {
                                                //Пропуск
                                            }
                                        }
                                        t.Commit();
                                    }


                                    if (!TryPrepareFloorProfile(firstRoomCurves, shortCurveTolerance, curveCreationTolerance, out List<Curve> preparedProfileCurves))
                                    {
                                        errorRooms.Add(room);
                                        continue;
                                    }

                                    t.Start("Создание плиты");
                                    Floor floor = null;
                                    try
                                    {
#if R2019 || R2020 || R2021 || R2022
                                        floor = doc.Create.NewFloor(ToCurveArray(preparedProfileCurves), typeFromParameter, roomLevel, false);
                                        floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(floorLevelOffset);
#else
                                        if (!TryCreateValidatedCurveLoops(preparedProfileCurves, shortCurveTolerance, curveCreationTolerance, out List<CurveLoop> curveLoopList))
                                        {
                                            errorRooms.Add(room);
                                            t.Commit();
                                            continue;
                                        }

                                        floor = Floor.Create(doc, curveLoopList, typeFromParameter.Id, roomLevel.Id);
                                        floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(floorLevelOffset);
#endif
                                    }
                                    catch
                                    {
                                        errorRooms.Add(room);
                                        t.Commit();
                                        continue;
                                    }
                                    //Удаление предупреждения о редактировании группы вне редактора
                                    FailureHandlingOptions failureHandlingOptions = t.GetFailureHandlingOptions();
                                    failureHandlingOptions.SetFailuresPreprocessor(new FloorIntersectionWarningSwallower());
                                    t.SetFailureHandlingOptions(failureHandlingOptions);
                                    //СОБРАТЬ ПРЕДУПРЕЖДЕНИЯ ПО ПОМЕЩЕНИЯМ!!!!

                                    t.Commit();
                                    t.Start("Вырезание проемов");
                                    if (secondRoomCurves.Size != 0)
                                    {
                                        try
                                        {
                                            doc.Create.NewOpening(floor, secondRoomCurves, true);
                                        }
                                        catch
                                        {

                                        }
                                    }
                                    t.Commit();
                                }
                                else
                                {
                                    //skippedRoomsList.Add(room);
                                }
                            }
                            floorCreatorProgressBarWPF.Dispatcher.Invoke(() => floorCreatorProgressBarWPF.Close());
                            transGroup.Assimilate();
                        }
                    }
                }
            }
            if (errorRooms.Count > 0)
            {
                // Создаем и показываем окно с ошибками
                ErrorRoomsDialogWPF errorDialog = new ErrorRoomsDialogWPF(errorRooms);
                errorDialog.ShowDialog();
            }

            return Result.Succeeded;
        }
        private static List<Room> GetRoomsFromCurrentSelection(Document doc, Selection sel)
        {
            var result = new List<Room>();
            if (doc == null || sel == null) return result;

            ICollection<ElementId> selectedIds = sel.GetElementIds();
            if (selectedIds == null || selectedIds.Count == 0) return result;

            foreach (ElementId id in selectedIds)
            {
                if (id == null || id == ElementId.InvalidElementId) continue;

                Element e = doc.GetElement(id);
                if (e == null) continue;

                // Быстро: сначала тип
                Room room = e as Room;
                if (room == null) continue;

#if R2019 || R2020 || R2021 || R2022 || R2023 || R2024 || R2025
                Category cat = e.Category;
                if (cat != null && cat.Id.IntegerValue == (int)BuiltInCategory.OST_Rooms)
                    result.Add(room);
#else
                Category cat = e.Category;
                if (cat != null && cat.Id.Value == (long)BuiltInCategory.OST_Rooms)
                    result.Add(room);
#endif
            }

            return result;
        }

        private void ThreadStartingPoint()
        {
            floorCreatorProgressBarWPF = new FloorCreatorProgressBarWPF();
            floorCreatorProgressBarWPF.Show();
            System.Windows.Threading.Dispatcher.Run();
        }
        private CurveArray ApplyDoorPatchesToRoomCurves(
            Document doc,
            Room room,
            IList<IList<BoundarySegment>> loops,
            CurveArray roomCurves,
            double shortCurveTolerance,
            double curveCreationTolerance)
        {
            if (doc == null || room == null || loops == null || roomCurves == null || roomCurves.Size < 3)
                return roomCurves;

            List<Curve> mainEdges = roomCurves.Cast<Curve>().ToList();
            if (mainEdges.Count < 3)
                return roomCurves;

            var doorPatches = new List<(XYZ p1, XYZ p2, XYZ p3, XYZ p4)>();

            var doorCollector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(d =>
                    (d.FromRoom != null && d.FromRoom.Id == room.Id) ||
                    (d.ToRoom != null && d.ToRoom.Id == room.Id))
                .ToList();

            foreach (var door in doorCollector)
            {
                var hostWall = door.Host as Wall;
                if (hostWall == null) continue;

                double width = door.Symbol?.get_Parameter(BuiltInParameter.GENERIC_WIDTH)?.AsDouble() ?? 0;
                if (width == 0)
                    width = door.get_Parameter(BuiltInParameter.GENERIC_WIDTH)?.AsDouble() ?? 0;
                if (width <= 0) continue;

                double halfWidth = width / 2.0;
                if (!(door.Location is LocationPoint locationPoint)) continue;

                XYZ origin = locationPoint.Point;
                BoundarySegment bestSeg = null;
                double minDist = double.MaxValue;
                foreach (var loop in loops)
                {
                    foreach (var seg in loop)
                    {
                        double distance = seg.GetCurve().Distance(origin);
                        if (distance < minDist)
                        {
                            minDist = distance;
                            bestSeg = seg;
                        }
                    }
                }

                if (bestSeg == null || minDist > 2.0) continue;

                Curve segCurve = bestSeg.GetCurve();
                XYZ p1;
                XYZ p2;
                XYZ p3;
                XYZ p4;

                if (segCurve is Line lineSegment)
                {
                    XYZ a = lineSegment.GetEndPoint(0);
                    XYZ b = lineSegment.GetEndPoint(1);
                    XYZ wallDir = (b - a).Normalize();
                    XYZ doorProj = ProjectPointOnLine(a, b, origin);

                    p1 = doorProj - wallDir * halfWidth;
                    p2 = doorProj + wallDir * halfWidth;

                    XYZ roomPt = (room.Location as LocationPoint)?.Point ??
                                 ((room.get_BoundingBox(null).Min + room.get_BoundingBox(null).Max) * 0.5);
                    XYZ perpDir = wallDir.CrossProduct(XYZ.BasisZ).Normalize();
                    if (perpDir.DotProduct((roomPt - doorProj).Normalize()) > 0)
                        perpDir = -perpDir;

                    double inset = (origin - doorProj).DotProduct(perpDir);
                    if (inset < 0) inset = 0;

                    p3 = p2 + perpDir * inset;
                    p4 = p1 + perpDir * inset;
                }
                else if (segCurve is Arc arcSegment)
                {
                    IntersectionResult projection = arcSegment.Project(origin);
                    if (projection == null) continue;

                    XYZ doorProj = projection.XYZPoint;
                    double tProj = projection.Parameter;
                    double radius = arcSegment.Radius;
                    double delta = radius > 1e-9 ? halfWidth / radius : 0.0;

                    p1 = arcSegment.Evaluate(tProj - delta, false);
                    p2 = arcSegment.Evaluate(tProj + delta, false);

                    XYZ roomPt = (room.Location as LocationPoint)?.Point ??
                                 ((room.get_BoundingBox(null).Min + room.get_BoundingBox(null).Max) * 0.5);
                    XYZ radial = (doorProj - arcSegment.Center).Normalize();
                    if (radial.DotProduct((roomPt - doorProj).Normalize()) > 0)
                        radial = -radial;

                    double inset = (origin - doorProj).DotProduct(radial);
                    if (inset < 0) inset = 0;

                    p3 = p2 + radial * inset;
                    p4 = p1 + radial * inset;
                }
                else
                {
                    continue;
                }

                var patch = (p1, p2, p3, p4);
                if (!IsDoorPatchLargeEnough(patch, curveCreationTolerance))
                    continue;

                doorPatches.Add(patch);
            }

            if (doorPatches.Count == 0)
                return roomCurves;

            var xyzEq = new XYZEquality(shortCurveTolerance);

            foreach (var patch in doorPatches)
            {
                for (int i = 0; i < mainEdges.Count; i++)
                {
                    Curve curve = mainEdges[i];

                    if (curve is Line line &&
                        IsPointOnLineSegment(line.GetEndPoint(0), line.GetEndPoint(1), patch.p1, shortCurveTolerance) &&
                        IsPointOnLineSegment(line.GetEndPoint(0), line.GetEndPoint(1), patch.p2, shortCurveTolerance))
                    {
                        if (TryBuildPatchedLineSegments(line, patch, xyzEq, curveCreationTolerance, out List<Curve> newEdges))
                        {
                            mainEdges.RemoveAt(i);
                            mainEdges.InsertRange(i, newEdges);
                        }
                        break;
                    }

                    if (curve is Arc arc &&
                        arc.Project(patch.p1).Distance < shortCurveTolerance &&
                        arc.Project(patch.p2).Distance < shortCurveTolerance)
                    {
                        if (TryBuildPatchedArcSegments(arc, patch, xyzEq, curveCreationTolerance, out List<Curve> newEdges))
                        {
                            mainEdges.RemoveAt(i);
                            mainEdges.InsertRange(i, newEdges);
                        }
                        break;
                    }
                }
            }

            CurveArray patchedCurves = new CurveArray();
            foreach (Curve curve in mainEdges)
            {
                patchedCurves.Append(curve);
            }

            return patchedCurves;
        }
        private CurveArray GetFilteredRoomCurves(IList<IList<BoundarySegment>> loops, double minLength, double createTolerance)
        {
            CurveArray filteredCurves = new CurveArray();

            if (loops == null || loops.Count == 0)
                return filteredCurves;

            var loop = loops[0];
            if (loop == null || loop.Count < 3)
                return filteredCurves;

            List<Curve> sourceCurves = loop
                .Select(seg => seg?.GetCurve())
                .Where(curve => curve != null)
                .ToList();

            if (sourceCurves.Count < 3)
                return filteredCurves;

            int startIndex = sourceCurves.FindIndex(curve => IsCurveLongEnough(curve, createTolerance));
            if (startIndex < 0)
                return filteredCurves;

            List<Curve> orderedCurves = new List<Curve>();
            for (int i = 0; i < sourceCurves.Count; i++)
            {
                orderedCurves.Add(sourceCurves[(startIndex + i) % sourceCurves.Count]);
            }

            List<Curve> tempCurves = new List<Curve>();
            foreach (Curve curve in orderedCurves)
            {
                if (IsCurveLongEnough(curve, createTolerance))
                {
                    tempCurves.Add(curve);
                    continue;
                }

                if (tempCurves.Count == 0)
                    continue;

                Curve lastCurve = tempCurves[tempCurves.Count - 1];
                if (!(lastCurve is Line lastLine) || !(curve is Line shortLine))
                    return new CurveArray();

                if (!ArePointsWithinTolerance(lastLine.GetEndPoint(1), shortLine.GetEndPoint(0), minLength))
                    return new CurveArray();

                if (!AreLinesNearlyCollinear(lastLine, lastLine.GetEndPoint(0), shortLine.GetEndPoint(1)))
                    return new CurveArray();

                if (!TryCreateSafeLine(lastLine.GetEndPoint(0), shortLine.GetEndPoint(1), createTolerance, out Line mergedLine))
                    return new CurveArray();

                tempCurves[tempCurves.Count - 1] = mergedLine;
            }

            if (tempCurves.Count < 3)
                return filteredCurves;

            if (!TrySnapClosedCurveChain(tempCurves, minLength, createTolerance))
                return new CurveArray();

            if (!AreCurvesContinuous(tempCurves, minLength))
                return new CurveArray();

            foreach (Curve curve in tempCurves)
            {
                if (!IsCurveLongEnough(curve, createTolerance))
                    return new CurveArray();

                filteredCurves.Append(curve);
            }

            return filteredCurves;
        }

        private static async Task GetPluginStartInfo()
        {
            // Получаем сборку, в которой выполняется текущий код
            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            string assemblyName = "FloorCreator";
            string assemblyNameRus = "Полы";
            string assemblyFolderPath = Path.GetDirectoryName(thisAssembly.Location);

            int lastBackslashIndex = assemblyFolderPath.LastIndexOf("\\");
            string dllPath = assemblyFolderPath.Substring(0, lastBackslashIndex + 1) + "PluginInfoCollector\\PluginInfoCollector.dll";

            Assembly assembly = Assembly.LoadFrom(dllPath);
            Type type = assembly.GetType("PluginInfoCollector.InfoCollector");

            if (type != null)
            {
                // Создание экземпляра класса
                object instance = Activator.CreateInstance(type);

                // Получение метода CollectPluginUsageAsync
                var method = type.GetMethod("CollectPluginUsageAsync");

                if (method != null)
                {
                    // Вызов асинхронного метода через reflection
                    Task task = (Task)method.Invoke(instance, new object[] { assemblyName, assemblyNameRus });
                    await task;  // Ожидание завершения асинхронного метода
                }
            }
        }

        // ----------- Хелперы ----------------------------------------
        private static bool TryPrepareFloorProfile(CurveArray sourceCurves, double continuityTolerance, double minCurveLength, out List<Curve> preparedCurves)
        {
            preparedCurves = new List<Curve>();
            if (sourceCurves == null || sourceCurves.Size < 3)
                return false;

            foreach (Curve curve in sourceCurves)
            {
                if (curve != null)
                    preparedCurves.Add(curve);
            }

            if (preparedCurves.Count < 3)
                return false;

            try
            {
                preparedCurves = SortCurves(preparedCurves, continuityTolerance);
            }
            catch
            {
                return false;
            }

            if (!TrySnapClosedCurveChain(preparedCurves, continuityTolerance, minCurveLength))
                return false;

            if (!AreCurvesContinuous(preparedCurves, continuityTolerance))
                return false;

            return preparedCurves.All(curve => IsCurveLongEnough(curve, minCurveLength));
        }

        private static bool TryCreateValidatedCurveLoops(List<Curve> preparedCurves, double continuityTolerance, double minCurveLength, out List<CurveLoop> curveLoopList)
        {
            curveLoopList = null;
            if (preparedCurves == null || preparedCurves.Count < 3)
                return false;

            if (!AreCurvesContinuous(preparedCurves, continuityTolerance))
                return false;

            if (preparedCurves.Any(curve => !IsCurveLongEnough(curve, minCurveLength)))
                return false;

            try
            {
                CurveLoop curveLoop = CurveLoop.Create(preparedCurves);
                curveLoopList = new List<CurveLoop> { curveLoop };
            }
            catch
            {
                return false;
            }

#if !R2019 && !R2020 && !R2021
            return BoundaryValidation.IsValidHorizontalBoundary(curveLoopList);
#else
            return true;
#endif
        }

        private static CurveArray ToCurveArray(IEnumerable<Curve> curves)
        {
            CurveArray curveArray = new CurveArray();
            if (curves == null)
                return curveArray;

            foreach (Curve curve in curves)
            {
                if (curve != null)
                    curveArray.Append(curve);
            }

            return curveArray;
        }

        private static bool TryBuildPatchedLineSegments(Line sourceLine, (XYZ p1, XYZ p2, XYZ p3, XYZ p4) patch, XYZEquality xyzEq, double minLength, out List<Curve> newEdges)
        {
            newEdges = new List<Curve>();
            XYZ a = sourceLine.GetEndPoint(0);
            XYZ b = sourceLine.GetEndPoint(1);

            if (!xyzEq.Equals(a, patch.p1) && !TryAddSafeLine(newEdges, a, patch.p1, minLength))
                return false;

            if (!TryAddSafeLine(newEdges, patch.p1, patch.p4, minLength))
                return false;
            if (!TryAddSafeLine(newEdges, patch.p4, patch.p3, minLength))
                return false;
            if (!TryAddSafeLine(newEdges, patch.p3, patch.p2, minLength))
                return false;

            if (!xyzEq.Equals(patch.p2, b) && !TryAddSafeLine(newEdges, patch.p2, b, minLength))
                return false;

            return newEdges.Count >= 3;
        }

        private static bool TryBuildPatchedArcSegments(Arc sourceArc, (XYZ p1, XYZ p2, XYZ p3, XYZ p4) patch, XYZEquality xyzEq, double minLength, out List<Curve> newEdges)
        {
            newEdges = new List<Curve>();

            IntersectionResult p1Projection = sourceArc.Project(patch.p1);
            IntersectionResult p2Projection = sourceArc.Project(patch.p2);
            if (p1Projection == null || p2Projection == null)
                return false;

            double t0 = sourceArc.GetEndParameter(0);
            double t1 = sourceArc.GetEndParameter(1);
            double tp1 = p1Projection.Parameter;
            double tp2 = p2Projection.Parameter;
            if (tp1 > tp2) (tp1, tp2) = (tp2, tp1);

            if (!xyzEq.Equals(sourceArc.Evaluate(t0, false), patch.p1))
            {
                if (!TryCreateArcSegment(sourceArc, t0, tp1, minLength, out Arc startArc))
                    return false;
                newEdges.Add(startArc);
            }

            if (!TryAddSafeLine(newEdges, patch.p1, patch.p4, minLength))
                return false;
            if (!TryAddSafeLine(newEdges, patch.p4, patch.p3, minLength))
                return false;
            if (!TryAddSafeLine(newEdges, patch.p3, patch.p2, minLength))
                return false;

            if (!xyzEq.Equals(patch.p2, sourceArc.Evaluate(t1, false)))
            {
                if (!TryCreateArcSegment(sourceArc, tp2, t1, minLength, out Arc endArc))
                    return false;
                newEdges.Add(endArc);
            }

            return newEdges.Count >= 3;
        }

        private static bool IsDoorPatchLargeEnough((XYZ p1, XYZ p2, XYZ p3, XYZ p4) patch, double minLength)
        {
            return patch.p1.DistanceTo(patch.p4) > minLength &&
                   patch.p4.DistanceTo(patch.p3) > minLength &&
                   patch.p3.DistanceTo(patch.p2) > minLength;
        }

        private static bool TryCreateArcSegment(Arc sourceArc, double startParameter, double endParameter, double minLength, out Arc arcSegment)
        {
            arcSegment = null;
            if (endParameter <= startParameter)
                return false;

            try
            {
                XYZ start = sourceArc.Evaluate(startParameter, false);
                XYZ end = sourceArc.Evaluate(endParameter, false);
                XYZ middle = sourceArc.Evaluate((startParameter + endParameter) * 0.5, false);

                if (start.DistanceTo(end) <= minLength)
                    return false;

                Arc candidate = Arc.Create(start, end, middle);
                if (!IsCurveLongEnough(candidate, minLength))
                    return false;

                arcSegment = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryAddSafeLine(List<Curve> curves, XYZ start, XYZ end, double minLength)
        {
            if (!TryCreateSafeLine(start, end, minLength, out Line line))
                return false;

            curves.Add(line);
            return true;
        }

        private static bool TryCreateSafeLine(XYZ start, XYZ end, double minLength, out Line line)
        {
            line = null;
            if (start == null || end == null)
                return false;

            if (start.DistanceTo(end) <= minLength)
                return false;

            try
            {
                line = Line.CreateBound(start, end);
                return IsCurveLongEnough(line, minLength);
            }
            catch
            {
                line = null;
                return false;
            }
        }

        private static bool TrySnapClosedCurveChain(List<Curve> curves, double continuityTolerance, double minCurveLength)
        {
            if (curves == null || curves.Count < 3)
                return false;

            XYZ firstStart = curves[0].GetEndPoint(0);
            XYZ lastEnd = curves[curves.Count - 1].GetEndPoint(1);
            double closureGap = firstStart.DistanceTo(lastEnd);

            if (closureGap > continuityTolerance)
                return false;

            if (closureGap > 0 &&
                curves[curves.Count - 1] is Line lastLine &&
                TryCreateSafeLine(lastLine.GetEndPoint(0), firstStart, minCurveLength, out Line snappedLine))
            {
                curves[curves.Count - 1] = snappedLine;
            }

            return true;
        }

        private static bool AreCurvesContinuous(IList<Curve> curves, double tolerance)
        {
            if (curves == null || curves.Count < 3)
                return false;

            for (int i = 0; i < curves.Count; i++)
            {
                Curve currentCurve = curves[i];
                Curve nextCurve = curves[(i + 1) % curves.Count];
                if (currentCurve == null || nextCurve == null)
                    return false;

                if (!ArePointsWithinTolerance(currentCurve.GetEndPoint(1), nextCurve.GetEndPoint(0), tolerance))
                    return false;
            }

            return true;
        }

        private static bool AreLinesNearlyCollinear(Line sourceLine, XYZ mergedStart, XYZ mergedEnd)
        {
            if (!TryGetDirection(sourceLine.GetEndPoint(0), sourceLine.GetEndPoint(1), out XYZ sourceDirection))
                return false;
            if (!TryGetDirection(mergedStart, mergedEnd, out XYZ mergedDirection))
                return false;

            return Math.Abs(sourceDirection.DotProduct(mergedDirection)) >= 0.999;
        }

        private static bool TryGetDirection(XYZ start, XYZ end, out XYZ direction)
        {
            direction = null;
            if (start == null || end == null)
                return false;

            XYZ delta = end - start;
            if (delta.GetLength() <= 1e-9)
                return false;

            direction = delta.Normalize();
            return true;
        }

        private static bool IsCurveLongEnough(Curve curve, double minLength)
        {
            return curve != null && curve.Length > minLength;
        }

        private static bool ArePointsWithinTolerance(XYZ first, XYZ second, double tolerance)
        {
            if (first == null || second == null)
                return false;

            return first.DistanceTo(second) <= tolerance;
        }

        bool IsPointOnLineSegment(XYZ a, XYZ b, XYZ p, double tol)
        {
            double ab = a.DistanceTo(b), ap = a.DistanceTo(p), pb = p.DistanceTo(b);
            return Math.Abs(ab - (ap + pb)) < tol;
        }
        XYZ ProjectPointOnLine(XYZ start, XYZ end, XYZ p)
        {
            XYZ dir = (end - start).Normalize();
            double proj = (p - start).DotProduct(dir);
            return start + dir * proj;
        }
        public static List<Curve> SortCurves(List<Curve> curves, double tolerance = 1e-4)
        {
            if (curves.Count == 0)
                return new List<Curve>();

            List<Curve> sorted = new List<Curve>();
            List<Curve> unused = new List<Curve>(curves);

            // Начинаем с первой кривой
            sorted.Add(unused[0]);
            unused.RemoveAt(0);

            while (unused.Count > 0)
            {
                XYZ lastEnd = sorted.Last().GetEndPoint(1);
                int idx = unused.FindIndex(c => c.GetEndPoint(0).IsAlmostEqualTo(lastEnd, tolerance));

                // Если не нашли — пробуем развернуть кривую (например, кто-то в другую сторону)
                if (idx == -1)
                {
                    idx = unused.FindIndex(c => c.GetEndPoint(1).IsAlmostEqualTo(lastEnd, tolerance));
                    if (idx != -1)
                    {
                        Curve reversed = unused[idx].CreateReversed();
                        unused[idx] = reversed;
                    }
                }

                if (idx == -1)
                {
                    // Не удалось замкнуть контур — разрыв
                    throw new Exception($"Не удалось замкнуть контур: разрыв после {sorted.Count - 1} сегмента. Оставшиеся кривые: {unused.Count}");
                }

                sorted.Add(unused[idx]);
                unused.RemoveAt(idx);
            }

            if (sorted.Count > 1 && !ArePointsWithinTolerance(sorted[sorted.Count - 1].GetEndPoint(1), sorted[0].GetEndPoint(0), tolerance))
            {
                throw new Exception("Не удалось замкнуть контур после сортировки.");
            }

            return sorted;
        }
    }
}
