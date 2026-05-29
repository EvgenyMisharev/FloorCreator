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

            bool needFillDoorPatches = floorCreatorWPF.FillDoorPatches;

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

                            double minLength = 0.5 / 304.8;
                            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                            CurveArray firstRoomCurves = GetFilteredRoomCurves(loops, minLength);
                            List<Curve> mainEdgs = firstRoomCurves.Cast<Curve>().ToList();

                            if (needFillDoorPatches)
                            {
                                var doorPatches = new List<(XYZ p1, XYZ p2, XYZ p3, XYZ p4)>();

                                var doorCollector = new FilteredElementCollector(doc)
                                    .OfCategory(BuiltInCategory.OST_Doors)
                                    .OfClass(typeof(FamilyInstance))
                                    .Cast<FamilyInstance>()
                                    .Where(d =>
                                        (d.FromRoom != null && d.FromRoom.Id == room.Id) ||
                                        (d.ToRoom != null && d.ToRoom.Id == room.Id))
                                    .ToList();

                                // ---------------- 1) Формируем точки patch ----------------
                                foreach (var door in doorCollector)
                                {
                                    var hostWall = door.Host as Wall;
                                    if (hostWall == null) continue;

                                    // ширина проёма
                                    double w = door.Symbol?.get_Parameter(BuiltInParameter.GENERIC_WIDTH)?.AsDouble() ?? 0;
                                    if (w == 0)
                                        w = door.get_Parameter(BuiltInParameter.GENERIC_WIDTH)?.AsDouble() ?? 0;
                                    if (w <= 0) continue;

                                    double halfW = w / 2.0;

                                    var lp = door.Location as LocationPoint;
                                    if (lp == null) continue;

                                    XYZ origin = lp.Point;

                                    // ближайший сегмент границы помещения (Line | Arc)
                                    BoundarySegment bestSeg = null; double minDist = double.MaxValue;
                                    foreach (var loop in loops)
                                    {
                                        foreach (var seg in loop)
                                        {
                                            double d = seg.GetCurve().Distance(origin);
                                            if (d < minDist) { minDist = d; bestSeg = seg; }
                                        }
                                    }
                                    if (bestSeg == null || minDist > 2.0) continue;

                                    var segCrv = bestSeg.GetCurve();

                                    XYZ p1, p2, p3, p4;

                                    if (segCrv is Line lnSeg) // ── прямой сегмент ──
                                    {
                                        XYZ a = lnSeg.GetEndPoint(0);
                                        XYZ b = lnSeg.GetEndPoint(1);
                                        XYZ wallDir = (b - a).Normalize();

                                        // проекция центра двери на линию сегмента
                                        XYZ doorProj = ProjectPointOnLine(a, b, origin);

                                        // точки вдоль сегмента по ширине проёма
                                        p1 = doorProj - wallDir * halfW;
                                        p2 = doorProj + wallDir * halfW;

                                        // перпендикуляр внутрь помещения
                                        XYZ roomPt = (room.Location as LocationPoint)?.Point ??
                                                     ((room.get_BoundingBox(null).Min + room.get_BoundingBox(null).Max) * 0.5);
                                        XYZ perpDir = wallDir.CrossProduct(XYZ.BasisZ).Normalize();
                                        if (perpDir.DotProduct((roomPt - doorProj).Normalize()) > 0)
                                            perpDir = -perpDir;

                                        // глубина завода как проекция «центр двери − проекция» на перпендикуляр
                                        double inset = (origin - doorProj).DotProduct(perpDir);
                                        if (inset < 0) inset = 0; // на всякий

                                        p3 = p2 + perpDir * inset;
                                        p4 = p1 + perpDir * inset;
                                    }
                                    else // ── дуговой сегмент ──
                                    {
                                        var arcSeg = (Arc)segCrv;

                                        // проекция центра двери на дугу
                                        var pr = arcSeg.Project(origin);
                                        XYZ doorProj = pr.XYZPoint;
                                        double tProj = pr.Parameter;

                                        // смещение вдоль дуги по половине ширины (радианы)
                                        double R = arcSeg.Radius;
                                        double delta = (R > 1e-9) ? (halfW / R) : 0.0;
                                        double t1 = tProj - delta;
                                        double t2 = tProj + delta;

                                        p1 = arcSeg.Evaluate(t1, false); // обе точки лежат на дуге
                                        p2 = arcSeg.Evaluate(t2, false);

                                        // касательная вдоль дуги (для построения p1/p2 уже не нужна дальше)
                                        // XYZ wallDir = (p2 - p1).Normalize();

                                        // радиальная нормаль внутрь помещения
                                        XYZ roomPt = (room.Location as LocationPoint)?.Point ??
                                                     ((room.get_BoundingBox(null).Min + room.get_BoundingBox(null).Max) * 0.5);
                                        XYZ radial = (doorProj - arcSeg.Center).Normalize();
                                        if (radial.DotProduct((roomPt - doorProj).Normalize()) > 0)
                                            radial = -radial;

                                        // глубина завода как проекция «центр двери − проекция» на радиальную нормаль
                                        double inset = (origin - doorProj).DotProduct(radial);
                                        if (inset < 0) inset = 0;

                                        p3 = p2 + radial * inset;
                                        p4 = p1 + radial * inset;
                                    }

                                    doorPatches.Add((p1, p2, p3, p4));
                                }

                                // ---------------- 2) Врезка в boundary (Line + Arc) ----------------
                                var xyzEq = new XYZEquality(1e-5);

                                foreach (var patch in doorPatches)
                                {
                                    bool inserted = false;

                                    for (int i = 0; i < mainEdgs.Count; i++)
                                    {
                                        Curve crv = mainEdgs[i];

                                        // ----- LINE ---------------------------------------------------
                                        if (crv is Line ln &&
                                            IsPointOnLineSegment(ln.GetEndPoint(0), ln.GetEndPoint(1), patch.p1, 1e-5) &&
                                            IsPointOnLineSegment(ln.GetEndPoint(0), ln.GetEndPoint(1), patch.p2, 1e-5))
                                        {
                                            var newEdges = new List<Curve>();
                                            XYZ a = ln.GetEndPoint(0), b = ln.GetEndPoint(1);

                                            if (!xyzEq.Equals(a, patch.p1)) newEdges.Add(Line.CreateBound(a, patch.p1));

                                            newEdges.Add(Line.CreateBound(patch.p1, patch.p4));
                                            newEdges.Add(Line.CreateBound(patch.p4, patch.p3));
                                            newEdges.Add(Line.CreateBound(patch.p3, patch.p2));

                                            if (!xyzEq.Equals(patch.p2, b)) newEdges.Add(Line.CreateBound(patch.p2, b));

                                            mainEdgs.RemoveAt(i); mainEdgs.InsertRange(i, newEdges);
                                            inserted = true; break;
                                        }

                                        // ----- ARC ----------------------------------------------------
                                        if (crv is Arc arc &&
                                            arc.Project(patch.p1).Distance < 1e-5 &&
                                            arc.Project(patch.p2).Distance < 1e-5)
                                        {
                                            var newEdges = new List<Curve>();

                                            double t0 = arc.GetEndParameter(0);
                                            double t1 = arc.GetEndParameter(1);
                                            double tp1 = arc.Project(patch.p1).Parameter;
                                            double tp2 = arc.Project(patch.p2).Parameter;
                                            if (tp1 > tp2) (tp1, tp2) = (tp2, tp1);

                                            Arc CutArc(double ts, double te)
                                            {
                                                XYZ s = arc.Evaluate(ts, false);
                                                XYZ e = arc.Evaluate(te, false);
                                                XYZ m = arc.Evaluate((ts + te) * 0.5, false);
                                                return Arc.Create(s, e, m);
                                            }

                                            if (!xyzEq.Equals(arc.Evaluate(t0, false), patch.p1))
                                                newEdges.Add(CutArc(t0, tp1));

                                            newEdges.Add(Line.CreateBound(patch.p1, patch.p4));
                                            newEdges.Add(Line.CreateBound(patch.p4, patch.p3));
                                            newEdges.Add(Line.CreateBound(patch.p3, patch.p2));

                                            if (!xyzEq.Equals(patch.p2, arc.Evaluate(t1, false)))
                                                newEdges.Add(CutArc(tp2, t1));

                                            mainEdgs.RemoveAt(i); mainEdgs.InsertRange(i, newEdges);
                                            inserted = true; break;
                                        }
                                    }
                                }

                                // ---------------- 3) Обновляем CurveArray --------------------------
                                firstRoomCurves.Clear();
                                foreach (var c in mainEdgs) firstRoomCurves.Append(c);
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

                                double minLength = 0.5 / 304.8;
                                IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                                CurveArray firstRoomCurves = GetFilteredRoomCurves(loops, minLength);
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
                                    .FirstOrDefault(ft => !string.IsNullOrEmpty(ft.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID).AsString()) &&
                                    ft.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID).AsString() == room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR).AsString());
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

                                double minLength = 0.5 / 304.8;
                                IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                                CurveArray firstRoomCurves = GetFilteredRoomCurves(loops, minLength);
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
                                    .FirstOrDefault(ft => !string.IsNullOrEmpty(ft.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID).AsString()) &&
                                    ft.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID).AsString() == room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR).AsString());
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
                                        curvesListToCurveLoop = SortCurves(curvesListToCurveLoop);

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
        private CurveArray GetFilteredRoomCurves(IList<IList<BoundarySegment>> loops, double minLength)
        {
            CurveArray filteredCurves = new CurveArray();

            if (loops == null || loops.Count == 0)
                return filteredCurves;

            var loop = loops[0]; // Только внешний контур
            List<Curve> tempCurves = new List<Curve>();

            foreach (BoundarySegment seg in loop)
            {
                Curve curve = seg.GetCurve();
                if (curve.Length < minLength && tempCurves.Count > 0)
                {
                    // Берём предыдущую кривую
                    Curve lastCurve = tempCurves[tempCurves.Count - 1];
                    XYZ lastStart = lastCurve.GetEndPoint(0);
                    // Удлиняем её до конца текущей короткой кривой
                    Line newLine = Line.CreateBound(lastStart, curve.GetEndPoint(1));
                    tempCurves[tempCurves.Count - 1] = newLine;
                }
                else
                {
                    tempCurves.Add(curve);
                }
            }

            // Доводим контур до замкнутого состояния (если вдруг последний короткий)
            if (tempCurves.Count >= 2)
            {
                XYZ firstStart = tempCurves[0].GetEndPoint(0);
                XYZ lastEnd = tempCurves[tempCurves.Count - 1].GetEndPoint(1);
                if (!firstStart.IsAlmostEqualTo(lastEnd, 1e-4))
                {
                    // Перестроим последний Line, чтобы точно замкнуть контур
                    Curve lastCurve = tempCurves[tempCurves.Count - 1];
                    if (lastCurve is Line)
                    {
                        Line newLine = Line.CreateBound(lastCurve.GetEndPoint(0), firstStart);
                        tempCurves[tempCurves.Count - 1] = newLine;
                    }
                    else
                    {
                        // Если дуга, добавляем отдельную линию замыкания (крайний случай)
                        tempCurves.Add(Line.CreateBound(lastEnd, firstStart));
                    }
                }
            }

            // Заполняем CurveArray
            foreach (var c in tempCurves)
                filteredCurves.Append(c);

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
            return sorted;
        }
    }
}
