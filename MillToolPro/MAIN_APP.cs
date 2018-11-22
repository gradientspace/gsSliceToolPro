using System;
using System.Collections.Generic;
using System.IO;

using Gtk;
using GLib;
using g3;
using gs;
using gs.info;

namespace CNCViewer
{


	class MainClass
	{
		public static Window MainWindow;
		public static SliceViewCanvas View;

		public static SingleMaterialFFFSettings LastSettings;


        public static bool SHOW_RELOADED_GCODE_PATHS = false;


		public static void Main(string[] args)
		{
			ExceptionManager.UnhandledException += delegate (UnhandledExceptionArgs expArgs) {
				Console.WriteLine(expArgs.ExceptionObject.ToString());
				expArgs.ExitApplication = true;
			};

			Gtk.Application.Init();

			MainWindow = new Window("gsCNCViewer");
			MainWindow.SetDefaultSize(900, 600);
			MainWindow.SetPosition(WindowPosition.Center);
			MainWindow.DeleteEvent += delegate {
				Gtk.Application.Quit();
			};

            //DMesh3 part = StandardMeshReader.ReadMesh("../../../sample_files/hemisphere_h2p4.obj");
            //DMesh3 stock = StandardMeshReader.ReadMesh("../../../sample_files/stock_5x5x2p5.obj");

            DMesh3 part = StandardMeshReader.ReadMesh("../../../sample_files/mechpart1.obj");
            DMesh3 stock = StandardMeshReader.ReadMesh("../../../sample_files/mechpart1_stock.obj");


            PrintMeshAssembly meshes = new PrintMeshAssembly();
            meshes.AddMesh(stock);
            meshes.AddMesh(part, PrintMeshOptions.Cavity());

            View = new SliceViewCanvas();
            MainWindow.Add(View);


            //DMesh3 tube_mesh = GenerateTubeMeshesForGCode("c:\\Users\\rms\\Downloads\\gear train.nc");
            //StandardMeshWriter.WriteMesh("../../../sample_output/tubes.obj", tube_mesh, WriteOptions.Defaults);

            string sPath = GenerateGCodeForMeshes(meshes);

            if (SHOW_RELOADED_GCODE_PATHS) {
                LoadGeneratedGCodeFile(sPath);
            }

            MainWindow.KeyReleaseEvent += Window_KeyReleaseEvent;

            // support drag-drop
            Gtk.TargetEntry[] target_table = new TargetEntry[] {
              new TargetEntry ("text/uri-list", 0, 0),
            };
            Gtk.Drag.DestSet(MainWindow, DestDefaults.All, target_table, Gdk.DragAction.Copy);
            MainWindow.DragDataReceived += MainWindow_DragDataReceived; ;


            MainWindow.ShowAll();

            Gtk.Application.Run();
        }




        static string GenerateGCodeForMeshes(PrintMeshAssembly meshes)
        {

            AxisAlignedBox3d bounds = meshes.TotalBounds;
            double top_z = bounds.Depth;

            // configure settings
            RepRapSettings settings = new RepRapSettings(RepRap.Models.Unknown);
            settings.GenerateSupport = false;
            settings.EnableBridging = false;

            int nSpeed = 1200;  // foam
            //int nSpeed = 700;   // wood

            settings.RapidTravelSpeed = nSpeed;
            settings.RapidExtrudeSpeed = nSpeed;
            settings.CarefulExtrudeSpeed = nSpeed;
            settings.OuterPerimeterSpeedX = 1.0;
            settings.ZTravelSpeed = nSpeed;
            settings.RetractSpeed = nSpeed;

            settings.LayerHeightMM = 4.0;
            settings.Machine.NozzleDiamMM = 6.35;

            settings.Machine.BedSizeXMM = 240;
            settings.Machine.BedSizeYMM = 190;

            settings.RetractDistanceMM = 1;
            settings.EnableRetraction = true;

            settings.ShellsFillNozzleDiamStepX = 0.5;
            settings.SolidFillNozzleDiamStepX = 0.9;
            settings.SolidFillBorderOverlapX = 0.5;

            LastSettings = settings.CloneAs<SingleMaterialFFFSettings>();

            System.Console.WriteLine("Slicing...");

            // slice meshes
            MeshPlanarMillSlicer slicer = new MeshPlanarMillSlicer() {
                LayerHeightMM = settings.LayerHeightMM,
                ToolDiameter = settings.Machine.NozzleDiamMM,
                ExpandStockAmount = 0.4*settings.Machine.NozzleDiamMM
            };
            slicer.Add(meshes);
            MeshPlanarMillSlicer.Result sliceResult = slicer.Compute();
            PlanarSliceStack slices = sliceResult.Clearing;

            System.Console.WriteLine("Generating GCode...");

            ToolpathSet accumPaths;
            GCodeFile genGCode = generate_cnc_test(sliceResult, settings, out accumPaths);

            System.Console.WriteLine("Writing GCode...");

            string sWritePath = "../../../sample_output/generated.nc";
            StandardGCodeWriter writer = new StandardGCodeWriter() {
                CommentStyle = StandardGCodeWriter.CommentStyles.Bracket
            };
            using (StreamWriter w = new StreamWriter(sWritePath)) {
                writer.WriteFile(genGCode, w);
            }

            //DMesh3 tube_mesh = GenerateTubeMeshesForGCode(sWritePath, settings.Machine.NozzleDiamMM);
            DMesh3 tube_mesh = GenerateTubeMeshesForGCode(sWritePath, 0.4);
            StandardMeshWriter.WriteMesh("../../../sample_output/generated_tubes.obj", tube_mesh, WriteOptions.Defaults);

            if ( SHOW_RELOADED_GCODE_PATHS == false) {
                View.SetPaths(accumPaths, settings);
                View.PathDiameterMM = (float)settings.Machine.NozzleDiamMM;
            }


            slices.Add(sliceResult.HorizontalFinish.Slices);
            slices.Slices.Sort((a, b) => { return a.Z.CompareTo(b.Z); });
            View.SetSlices(slices);
            View.CurrentLayer = slices.Slices.Count - 1;

            return sWritePath;
        }





        static GCodeFile generate_cnc_test(MeshPlanarMillSlicer.Result sliceSets, RepRapSettings settings, out ToolpathSet AccumulatedPaths)
        {
            int PLUNGE_SPEED = 800;

            AccumulatedPaths = new ToolpathSet();

            GCodeFileAccumulator file_accumulator = new GCodeFileAccumulator();
            GCodeBuilder builder = new GCodeBuilder(file_accumulator);
            BaseThreeAxisMillingCompiler Compiler = new BaseThreeAxisMillingCompiler(builder, settings, GenericMillingAssembler.Factory);
            Compiler.Begin();

            /*
             * Clearing pass
             */

            PlanarSliceStack clearingSlices = sliceSets.Clearing;
            int N = clearingSlices.Count;

            // assuming origin is at top of stock so we actaully are going down in Z layers
            for (int k = 0; k < N; ++k)
                clearingSlices[k].Z = -(sliceSets.TopZ - clearingSlices[k].Z);

            for (int layeri = 0; layeri < N; layeri++) {
                PlanarSlice slice = clearingSlices[layeri];
                Compiler.AppendComment(string.Format("clearing layer {0} - {1}mm", layeri, slice.Z));

                ToolpathSetBuilder layer_builder = new ToolpathSetBuilder() {
                    MoveType = ToolpathTypes.Cut
                };
                layer_builder.Initialize(Compiler.ToolPosition);

                // To do a layer-change, we need to plunge down at the first scheduled cutting position.
                // However we will not know that until we schedule the first set of paths.
                // So, we configure SequentialScheduler2d to call this function when it knows this information,
                // and then we can travel to the new XY position before we plunge to new Z
                Action<List<FillCurveSet2d>, SequentialScheduler2d> DoZChangeF = (curves,scheduler) => {
                    Vector2d startPosXY = (curves[0].Loops.Count > 0) ?
                        curves[0].Loops[0].Start : curves[0].Curves[0].Start;
                    Vector3d startPosXYZ = new Vector3d(startPosXY.x, startPosXY.y, slice.Z);
                    // TODO: we should retract at faster speed here? maybe use custom function that does this better?
                    layer_builder.AppendTravel(startPosXYZ, PLUNGE_SPEED);
                    scheduler.OnAppendCurveSetsF = null;
                };

                SequentialScheduler2d layerScheduler = new SequentialScheduler2d(layer_builder, settings) {
                    ExtrudeOnShortTravels = true,
                    ShortTravelDistance = settings.Machine.NozzleDiamMM * 1.5
                };
                layerScheduler.OnAppendCurveSetsF = DoZChangeF;
                GroupScheduler2d groupScheduler = new GroupScheduler2d(layerScheduler, Compiler.ToolPosition.xy);

                foreach (GeneralPolygon2d shape in slice.Solids) {
                    ShellsFillPolygon shells_gen = new ShellsFillPolygon(shape) {
                        PathSpacing = settings.ShellsFillPathSpacingMM(),
                        ToolWidth = settings.Machine.NozzleDiamMM,
                        Layers = 10,
                        OuterShellLast = false,
                        DiscardTinyPerimterLengthMM = 1, DiscardTinyPolygonAreaMM2 = 1
                    };
                    shells_gen.Compute();

                    groupScheduler.BeginGroup();
                    groupScheduler.AppendCurveSets(shells_gen.Shells);
                    groupScheduler.EndGroup();
                }

                Compiler.AppendPaths(layer_builder.Paths, settings);
                AccumulatedPaths.Append(layer_builder.Paths);
            }






            /*
             * Horizontal finish pass
             */

            PlanarSliceStack horzSlices = sliceSets.HorizontalFinish;
            int NH = horzSlices.Count;

            // assuming origin is at top of stock so we actaully are going down in Z layers
            for (int k = 0; k < NH; ++k)
                horzSlices[k].Z = -(sliceSets.TopZ - horzSlices[k].Z);

            for (int layeri = 0; layeri < NH; layeri++) {
                PlanarSlice slice = horzSlices[layeri];
                Compiler.AppendComment(string.Format("horz finish layer {0} - {1}mm", layeri, slice.Z));

                ToolpathSetBuilder layer_builder = new ToolpathSetBuilder() {
                    MoveType = ToolpathTypes.Cut
                };
                layer_builder.Initialize(Compiler.ToolPosition);

                Action<List<FillCurveSet2d>, SequentialScheduler2d> DoZChangeF = (curves, scheduler) => {
                    Vector2d startPosXY = (curves[0].Loops.Count > 0) ?
                        curves[0].Loops[0].Start : curves[0].Curves[0].Start;
                    Vector3d startPosXYZ = new Vector3d(startPosXY.x, startPosXY.y, slice.Z);
                    // TODO: we should retract at faster speed here? maybe use custom function that does this better?
                    layer_builder.AppendTravel(startPosXYZ, PLUNGE_SPEED);
                    scheduler.OnAppendCurveSetsF = null;
                };

                SequentialScheduler2d layerScheduler = new SequentialScheduler2d(layer_builder, settings) {
                    ExtrudeOnShortTravels = true,
                    ShortTravelDistance = settings.Machine.NozzleDiamMM * 1.5
                };
                layerScheduler.OnAppendCurveSetsF = DoZChangeF;
                GroupScheduler2d groupScheduler = new GroupScheduler2d(layerScheduler, Compiler.ToolPosition.xy);

                foreach (GeneralPolygon2d shape in slice.Solids) {
                    ShellsFillPolygon shells_gen = new ShellsFillPolygon(shape) {
                        //InsetFromInputPolygonX = 0.0,
                        PathSpacing = settings.ShellsFillPathSpacingMM(),
                        ToolWidth = settings.Machine.NozzleDiamMM,
                        Layers = 10,
                        OuterShellLast = false,
                        PreserveInputInsetTopology = false,
                        DiscardTinyPerimterLengthMM = 1, DiscardTinyPolygonAreaMM2 = 1
                    };
                    shells_gen.Compute();

                    groupScheduler.BeginGroup();
                    groupScheduler.AppendCurveSets(shells_gen.Shells);
                    groupScheduler.EndGroup();
                }

                Compiler.AppendPaths(layer_builder.Paths, settings);
                AccumulatedPaths.Append(layer_builder.Paths);
            }


            // return to home position
            ToolpathSetBuilder finishBuilder = new ToolpathSetBuilder() {
                MoveType = ToolpathTypes.Cut
            };
            finishBuilder.Initialize(Compiler.ToolPosition);
            finishBuilder.AppendTravel(Vector3d.Zero, PLUNGE_SPEED);
            Compiler.AppendPaths(finishBuilder.Paths, settings);


            Compiler.End();
            return file_accumulator.File;
        }








        static void LoadGCodeFile(string sPath) {
			GenericGCodeParser parser = new GenericGCodeParser();
			GCodeFile gcode;
			using (FileStream fs = new FileStream(sPath, FileMode.Open, FileAccess.Read)) {
				using (TextReader reader = new StreamReader(fs)) {
					gcode = parser.Parse(reader);
				}
			}

			GCodeToToolpaths converter = new GCodeToToolpaths();
			MakerbotInterpreter interpreter = new MakerbotInterpreter();
			interpreter.AddListener(converter);
			InterpretArgs interpArgs = new InterpretArgs();
			interpreter.Interpret(gcode, interpArgs);

			ToolpathSet Paths = converter.PathSet;
			View.SetPaths(Paths);		
		}





        static void LoadGeneratedGCodeFile(string sPath)
        {
            // read gcode file
            GenericGCodeParser parser = new GenericGCodeParser();
            GCodeFile gcode;
            using (FileStream fs = new FileStream(sPath, FileMode.Open, FileAccess.Read)) {
                using (TextReader reader = new StreamReader(fs)) {
                    gcode = parser.Parse(reader);
                }
            }

            // write back out gcode we loaded
            //StandardGCodeWriter writer = new StandardGCodeWriter();
            //using ( StreamWriter w = new StreamWriter("../../../sample_output/writeback.nc") ) {
            //	writer.WriteFile(gcode, w);
            //}

            GCodeToToolpaths converter = new GCodeToToolpaths();
            ThreeAxisCNCInterpreter interpreter = new ThreeAxisCNCInterpreter();
            interpreter.AddListener(converter);

            InterpretArgs interpArgs = new InterpretArgs();
            interpreter.Interpret(gcode, interpArgs);

            View.SetPaths(converter.PathSet);
            if (LastSettings != null)
                View.PathDiameterMM = (float)LastSettings.Machine.NozzleDiamMM;
        }






        static DMesh3 GenerateTubeMeshesForGCode(string sPath, double pathWidth = 0.4)
        {
            GenericGCodeParser parser = new GenericGCodeParser();
            GCodeFile gcode;
            using (FileStream fs = new FileStream(sPath, FileMode.Open, FileAccess.Read)) {
                using (TextReader reader = new StreamReader(fs)) {
                    gcode = parser.Parse(reader);
                }
            }
            GCodeToLayerTubeMeshes make_tubes = new GCodeToLayerTubeMeshes() {
                TubeProfile = Polygon2d.MakeCircle(pathWidth/2, 12),
                InterpretZChangeAsLayerChange = false
            };
            make_tubes.WantTubeTypes.Add(ToolpathTypes.Travel);
            ThreeAxisCNCInterpreter interpreter = new ThreeAxisCNCInterpreter();
            interpreter.AddListener(make_tubes);
            interpreter.Interpret(gcode, new InterpretArgs());
            DMesh3 tubeMesh2 = make_tubes.GetCombinedMesh(1);
            return tubeMesh2;
        }







		void OnException(object o, UnhandledExceptionArgs args)
		{

		}


		private static void Window_KeyReleaseEvent(object sender, KeyReleaseEventArgs args)
		{
			if (args.Event.Key == Gdk.Key.Up) {
				if ((args.Event.State & Gdk.ModifierType.ShiftMask) != 0)
					View.CurrentLayer = View.CurrentLayer + 10;
				else
					View.CurrentLayer = View.CurrentLayer + 1;
			} else if (args.Event.Key == Gdk.Key.Down) {
				if ((args.Event.State & Gdk.ModifierType.ShiftMask) != 0)
					View.CurrentLayer = View.CurrentLayer - 10;
				else
					View.CurrentLayer = View.CurrentLayer - 1;

			} else if (args.Event.Key == Gdk.Key.n) {
				if (View.NumberMode == SliceViewCanvas.NumberModes.NoNumbers)
					View.NumberMode = SliceViewCanvas.NumberModes.PathNumbers;
				else
					View.NumberMode = SliceViewCanvas.NumberModes.NoNumbers;
				View.QueueDraw();

			} else if (args.Event.Key == Gdk.Key.f) {
				View.ShowFillArea = !View.ShowFillArea;
				View.QueueDraw();

			} else if (args.Event.Key == Gdk.Key.t) {
				View.ShowTravels = !View.ShowTravels;
				View.QueueDraw();

			} else if (args.Event.Key == Gdk.Key.e) {
				View.ShowDepositMoves = !View.ShowDepositMoves;
				View.QueueDraw();

            } else if (args.Event.Key == Gdk.Key.p) {
                View.ShowAllPathPoints = !View.ShowAllPathPoints;
                View.QueueDraw();

            } else if (args.Event.Key == Gdk.Key.b) {
				View.ShowBelowLayer = !View.ShowBelowLayer;
				View.QueueDraw();

            } else if (args.Event.Key == Gdk.Key.i) {
                View.ShowIssues = !View.ShowIssues;
                View.QueueDraw();

            } else if ( args.Event.Key == Gdk.Key.E ) {
                List<PolyLine2d> paths = View.GetPolylinesForLayer(View.CurrentLayer);
                SVGWriter writer = new SVGWriter();
                SVGWriter.Style lineStyle = SVGWriter.Style.Outline("black", 0.2f);
                foreach (var p in paths)
                    writer.AddPolyline(p, lineStyle);
                writer.Write("c:\\scratch\\__LAST_PATHS.svg");

            }
		}






		static void MainWindow_DragDataReceived(object o, DragDataReceivedArgs args)
		{
			string data = System.Text.Encoding.UTF8.GetString(args.SelectionData.Data);
			data = data.Trim('\r', '\n', '\0');
			if (Util.IsRunningOnMono()) {
				data = data.Replace("file://", "");
			} else {
				data = data.Replace("file:///", "");
			}
			data = data.Replace("%20", " ");        // gtk inserts these for spaces? maybe? wtf.
			try {
                LoadGCodeFile(data);
			} catch (Exception e) {
				using (var dialog = new Gtk.MessageDialog(MainWindow, DialogFlags.Modal, MessageType.Info, ButtonsType.Ok,
					"Exception loading {0} : {1}", data, e.Message)) {
					dialog.Show();
				}
			}
		}





	}
}
