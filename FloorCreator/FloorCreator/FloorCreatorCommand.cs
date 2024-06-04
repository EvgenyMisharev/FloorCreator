using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

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
                GetPluginStartInfo();
            }
            catch { }

            Document doc = commandData.Application.ActiveUIDocument.Document;
            Selection sel = commandData.Application.ActiveUIDocument.Selection;

            //Типы полов для формы
            List<FloorType> floorTypesList = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Where(f => f.Category.Id.IntegerValue.Equals((int)BuiltInCategory.OST_Floors))
                .Where(f => f.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL) != null)
                .Where(f => f.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL).AsString() == "Пол"
                || f.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL).AsString() == "Полы")
                .Cast<FloorType>()
                .OrderBy(f => f.Name, new AlphanumComparatorFastString())
                .ToList();

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
                            CurveArray firstRoomCurves = new CurveArray();
                            CurveArray secondRoomCurves = new CurveArray();
                            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                            for (int i = 0; i < loops.Count(); i++)
                            {
                                if (i == 0)
                                {
                                    foreach (BoundarySegment seg in loops[i])
                                    {
                                        firstRoomCurves.Append(seg.GetCurve());
                                    }
                                }
                                else
                                {
                                    foreach (BoundarySegment seg in loops[i])
                                    {
                                        secondRoomCurves.Append(seg.GetCurve());
                                    }
                                }
                            }

                            //Удаление старого пола
                            List<Floor> floorList = new FilteredElementCollector(doc)
                                .OfClass(typeof(Floor))
                                .Where(f => f.LevelId == room.LevelId)
                                .Cast<Floor>()
                                .Where(f => f.Category.Id.IntegerValue.Equals((int)BuiltInCategory.OST_Floors))
                                .Where(f => f.FloorType.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL).AsString() == "Пол"
                                || f.FloorType.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL).AsString() == "Полы")
                                .OrderBy(f => f.Name)
                                .ToList();

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

                            //Создание нового пола
                            t.Start("Создание плиты");

                            Floor floor = null;
                            try
                            {
#if R2019 || R2020 || R2021 || R2022
                                floor = doc.Create.NewFloor(firstRoomCurves, selectedFloorType, roomLevel, false);
                                floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(floorLevelOffset);
#else
                                List<Curve> curvesListToCurveLoop = new List<Curve>();
                                foreach (Curve c in firstRoomCurves)
                                {
                                    curvesListToCurveLoop.Add(c);
                                }
                                CurveLoop cl = CurveLoop.Create(curvesListToCurveLoop);
                                List<CurveLoop> curveLoopList = new List<CurveLoop>();
                                curveLoopList.Add(cl);

                                floor = Floor.Create(doc, curveLoopList, selectedFloorType.Id, roomLevel.Id);
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
                                CurveArray firstRoomCurves = new CurveArray();
                                CurveArray secondRoomCurves = new CurveArray();
                                IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                                for (int i = 0; i < loops.Count(); i++)
                                {
                                    if (i == 0)
                                    {
                                        foreach (BoundarySegment seg in loops[i])
                                        {
                                            firstRoomCurves.Append(seg.GetCurve());
                                        }
                                    }
                                    else
                                    {
                                        foreach (BoundarySegment seg in loops[i])
                                        {
                                            secondRoomCurves.Append(seg.GetCurve());
                                        }
                                    }
                                }

                                //Удаление старого пола
                                List<Floor> floorList = new FilteredElementCollector(doc)
                                    .OfClass(typeof(Floor))
                                    .Where(f => f.LevelId == room.LevelId)
                                    .Cast<Floor>()
                                    .Where(f => f.Category.Id.IntegerValue.Equals((int)BuiltInCategory.OST_Floors))
                                    .Where(f => f.FloorType.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL).AsString() == "Пол"
                                    || f.FloorType.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL).AsString() == "Полы")
                                    .OrderBy(f => f.Name)
                                    .ToList();

                                FloorType typeFromParameter = floorTypesList
                                    .FirstOrDefault(ft => ft.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID).AsString() == room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR).AsString());
                                if (typeFromParameter != null)
                                {
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

                                    t.Start("Создание плиты");
                                    Floor floor = null;
                                    try
                                    {
#if R2019 || R2020 || R2021 || R2022
                                        floor = doc.Create.NewFloor(firstRoomCurves, typeFromParameter, roomLevel, false);
                                        floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(floorLevelOffset);
#else
                                        List<Curve> curvesListToCurveLoop = new List<Curve>();
                                        foreach (Curve c in firstRoomCurves)
                                        {
                                            curvesListToCurveLoop.Add(c);
                                        }
                                        CurveLoop cl = CurveLoop.Create(curvesListToCurveLoop);
                                        List<CurveLoop> curveLoopList = new List<CurveLoop>();
                                        curveLoopList.Add(cl);
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
                                CurveArray firstRoomCurves = new CurveArray();
                                CurveArray secondRoomCurves = new CurveArray();
                                IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                                for (int i = 0; i < loops.Count(); i++)
                                {
                                    if (i == 0)
                                    {
                                        foreach (BoundarySegment seg in loops[i])
                                        {
                                            firstRoomCurves.Append(seg.GetCurve());
                                        }
                                    }
                                    else
                                    {
                                        foreach (BoundarySegment seg in loops[i])
                                        {
                                            secondRoomCurves.Append(seg.GetCurve());
                                        }
                                    }
                                }

                                //Удаление старого пола
                                List<Floor> floorList = new FilteredElementCollector(doc)
                                    .OfClass(typeof(Floor))
                                    .Where(f => f.LevelId == room.LevelId)
                                    .Cast<Floor>()
                                    .Where(f => f.Category.Id.IntegerValue.Equals((int)BuiltInCategory.OST_Floors))
                                    .Where(f => f.FloorType.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL).AsString() == "Пол"
                                    || f.FloorType.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL).AsString() == "Полы")
                                    .OrderBy(f => f.Name)
                                    .ToList();
                                FloorType typeFromParameter = floorTypesList
                                    .FirstOrDefault(ft => ft.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID).AsString() == room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR).AsString());
                                if (typeFromParameter != null)
                                {
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


                                    t.Start("Создание плиты");
                                    Floor floor = null;
                                    try
                                    {
#if R2019 || R2020 || R2021 || R2022
                                        floor = doc.Create.NewFloor(firstRoomCurves, typeFromParameter, roomLevel, false);
                                        floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(floorLevelOffset);
#else
                                        List<Curve> curvesListToCurveLoop = new List<Curve>();
                                        foreach (Curve c in firstRoomCurves)
                                        {
                                            curvesListToCurveLoop.Add(c);
                                        }
                                        CurveLoop cl = CurveLoop.Create(curvesListToCurveLoop);
                                        List<CurveLoop> curveLoopList = new List<CurveLoop>();
                                        curveLoopList.Add(cl);
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
        private static void GetPluginStartInfo()
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
            var constructor = type.GetConstructor(new Type[] { typeof(string), typeof(string) });

            if (type != null)
            {
                // Создание экземпляра класса
                object instance = Activator.CreateInstance(type, new object[] { assemblyName, assemblyNameRus });
            }
        }
        private static List<Room> GetRoomsFromCurrentSelection(Document doc, Selection sel)
        {
            ICollection<ElementId> selectedIds = sel.GetElementIds();
            List<Room> tempRoomsList = new List<Room>();
            foreach (ElementId roomId in selectedIds)
            {
                if (doc.GetElement(roomId) is Room
                    && null != doc.GetElement(roomId).Category
                    && doc.GetElement(roomId).Category.Id.IntegerValue.Equals((int)BuiltInCategory.OST_Rooms))
                {
                    tempRoomsList.Add(doc.GetElement(roomId) as Room);
                }
            }
            return tempRoomsList;
        }
        private void ThreadStartingPoint()
        {
            floorCreatorProgressBarWPF = new FloorCreatorProgressBarWPF();
            floorCreatorProgressBarWPF.Show();
            System.Windows.Threading.Dispatcher.Run();
        }

    }
}
