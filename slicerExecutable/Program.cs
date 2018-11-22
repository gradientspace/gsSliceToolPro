using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using g3;
using gs;
using gs.info;

namespace slicerExecutable
{
    class Program
    {
        const double MAX_DIM_MM = 77.3;
        const int MAX_TRI_COUNT = 100000;

        struct GCodeInfo
        {
            public int SliceCount;
            public AxisAlignedBox3d SliceBounds;

            public int GCodeLines;
            public int GCodeBytes;
            public AxisAlignedBox3d PathBounds;
            public AxisAlignedBox3d ExtrudeBounds;
            public double TotalLength;
        }



        static void Main(string[] args)
        {
            GCodeInfo info = new GCodeInfo();

            string filename = args[0];

            DMesh3 mesh = StandardMeshReader.ReadMesh(filename);
            AxisAlignedBox3d bounds = mesh.CachedBounds;
            MeshTransforms.Scale(mesh, MAX_DIM_MM / bounds.MaxDim);
            Vector3d basePt = mesh.CachedBounds.Point(0, 0, -1);
            MeshTransforms.Translate(mesh, -basePt);

            if (mesh.TriangleCount > MAX_TRI_COUNT) {
                Reducer r = new Reducer(mesh);
                r.ReduceToTriangleCount(MAX_TRI_COUNT);
                mesh = new DMesh3(mesh, true);
            }

            var start = DateTime.Now;

            bool ENABLE_SUPPORT_ZSHIFT = true;

            try {
                // configure settings
                MakerbotSettings settings = new MakerbotSettings(Makerbot.Models.Replicator2);
                //MonopriceSettings settings = new MonopriceSettings(Monoprice.Models.MP_Select_Mini_V2);
                //PrintrbotSettings settings = new PrintrbotSettings(Printrbot.Models.Plus);
                settings.ExtruderTempC = 200;
                settings.Shells = 2;
                settings.InteriorSolidRegionShells = 0;
                settings.SparseLinearInfillStepX = 10;
                settings.ClipSelfOverlaps = false;

                settings.GenerateSupport = true;
                settings.EnableSupportShell = true;

                PrintMeshAssembly meshes = new PrintMeshAssembly();
                meshes.AddMesh(mesh);

                // slice meshes
                MeshPlanarSlicerPro slicer = new MeshPlanarSlicerPro() {
                    LayerHeightMM = settings.LayerHeightMM,
                    SliceFactoryF = PlanarSlicePro.FactoryF
                };
                slicer.Add(meshes);
                PlanarSliceStack slices = slicer.Compute();
                info.SliceCount = slices.Count;
                info.SliceBounds = slices.Bounds;

                // run print generator
                SingleMaterialFFFPrintGenPro printGen =
                    new SingleMaterialFFFPrintGenPro(meshes, slices, settings);

                if (ENABLE_SUPPORT_ZSHIFT)
                    printGen.LayerPostProcessor = new SupportConnectionPostProcessor() { ZOffsetMM = 0.2f };
                printGen.AccumulatePathSet = true;

                printGen.Generate();

                GCodeFile genGCode = printGen.Result;

                info.PathBounds = printGen.AccumulatedPaths.Bounds;
                info.ExtrudeBounds = printGen.AccumulatedPaths.ExtrudeBounds;
                info.TotalLength = CurveUtils.ArcLength(printGen.AccumulatedPaths.AllPositionsItr());
                info.GCodeLines = genGCode.LineCount;

                // write to in-memory string
                StandardGCodeWriter writer = new StandardGCodeWriter();
                using (MemoryStream membuf = new MemoryStream()) {
                    using (StreamWriter w = new StreamWriter(membuf)) {
                        writer.WriteFile(genGCode, w);
                        info.GCodeBytes = (int)membuf.Length;
                    }
                }

                // try to force destructor error
                printGen = null;
                genGCode = null;
                GC.Collect();

            } catch (Exception e) {
                System.Console.WriteLine("EXCEPTION:" + e.Message);
                return;
            }

            var end = DateTime.Now;
            int seconds = (int)(end - start).TotalSeconds;

            System.Console.WriteLine("{0},{1},{2},{3},{4},{5},{6},{7},",
                filename, mesh.TriangleCount, "OK", seconds, info.SliceCount, info.GCodeLines, info.GCodeBytes, (int)info.TotalLength);
        }
    }
}
