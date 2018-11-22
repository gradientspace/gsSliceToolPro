using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using g3;
using gs;
using gs.info;

namespace slicer_TESTS
{
    class Program
    {
        const string CACHE_FILENAME = "..\\..\\..\\sample_output\\SLICEALL_DONE.cache.txt";

        static void Main(string[] args)
        {
            run_multi_process();
            //run_single_process();
        }


        static void run_multi_process()
        {
            int done_count = 0;
            int MAX_COUNT = 10000;
            bool VERBOSE = false;
            TimeSpan TIMEOUT = TimeSpan.FromSeconds(120);
            int SAVE_INCREMENT = 10;

            int failed_count = 0;

            HashSet<string> completed =
                File.Exists(CACHE_FILENAME) ? new HashSet<string>(File.ReadAllLines(CACHE_FILENAME)) : new HashSet<string>();

            string[] files = Directory.GetFiles("E:\\Thingi10K\\closed");
            //string[] files = File.ReadAllLines("..\\..\\..\\sample_output\\slice_over_30.txt");
            //string[] files = File.ReadAllLines("..\\..\\..\\sample_output\\slice_over_180.txt");
            //string[] files = File.ReadAllLines("..\\..\\..\\sample_output\\slice_fails.txt");
            //files = new string[] { "E:\\Thingi10K\\closed\\61464.g3mesh" };

            SafeListBuilder<string> result_strings = new SafeListBuilder<string>();
            SafeListBuilder<string> processed_files = new SafeListBuilder<string>();
            //gParallel.ForEach_Sequential(files, (filename) => {
            Parallel.ForEach(files,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                (filename) => {
                    if (!File.Exists(filename))
                        return;

                int i = done_count;
                if (i > MAX_COUNT)
                    return;
                Interlocked.Increment(ref done_count);
                if (i % SAVE_INCREMENT == 0) {
                    System.Console.WriteLine("started {0} / {1}", i, files.Length);
                }

                if (completed.Contains(filename))
                    return;

                // save progress on this run
                if (i % SAVE_INCREMENT == 0) {
                    write_output(result_strings);
                    lock (completed) {
                        write_completed(completed, CACHE_FILENAME);
                    }
                }

                StringBuilder builder = new StringBuilder();
                builder.Append(filename); builder.Append(',');

                var start = DateTime.Now;

                if (VERBOSE) System.Console.WriteLine(builder.ToString());

                GCodeInfo gcinfo = GenerateGCodeForFileWithTimeoutInProcess(filename, TIMEOUT);
                if (gcinfo.exception != null) {
                    System.Console.WriteLine(filename + " : " + gcinfo.exception.Message);
                    Interlocked.Increment(ref failed_count);
                }

                builder.Append(gcinfo.triangle_count.ToString()); builder.Append(',');
                
                if (gcinfo.timed_out) builder.Append("TIMEOUT");
                else if (gcinfo.completed) builder.Append("OK"); 
                else builder.Append("FAILED");
                builder.Append(',');

                var end = DateTime.Now;
                builder.Append(gcinfo.time_in_seconds.ToString()); builder.Append(',');

                builder.Append(gcinfo.SliceCount.ToString()); builder.Append(',');
                builder.Append(gcinfo.GCodeLines.ToString()); builder.Append(',');
                builder.Append(gcinfo.GCodeBytes.ToString()); builder.Append(',');
                builder.Append(gcinfo.TotalLength.ToString()); builder.Append(',');

                if (VERBOSE) System.Console.WriteLine(builder.ToString());
                result_strings.SafeAdd(builder.ToString());

                lock (completed) {
                    completed.Add(filename);
                }
            });

            write_output(result_strings);
        }






        static GCodeInfo GenerateGCodeForFileWithTimeoutInProcess(string filename, TimeSpan timeout)
        {
            string EXEPATH = "C:\\git\\gsSliceToolPro\\slicerExecutable\\bin\\Release\\slicerExecutable.exe";

            DateTime start = DateTime.Now;

            GCodeInfo gcinfo = new GCodeInfo();
            gcinfo.completed = false;

            bool aborted = false;
            Action<string, string> errorF = (msg, stack) => {
                if (aborted == false)
                    gcinfo.exception = new Exception(msg);
            };
            Func<bool> cancelF = () => {
                return aborted;
            };

            ProcessStartInfo pi = new ProcessStartInfo();
            pi.FileName = EXEPATH;
            pi.Arguments = filename;
            pi.UseShellExecute = false;
            pi.RedirectStandardOutput = true;
            Process p = Process.Start(pi);

            while (p.HasExited == false) {
                Thread.Sleep(1000);
                if ((DateTime.Now - start) > timeout) {
                    try {
                        p.Kill();
                    } catch { }
                    gcinfo.timed_out = true;
                    gcinfo.time_in_seconds = 999999;
                    break;
                }
            }
            if (gcinfo.timed_out)
                return gcinfo;

            try {
                string output = p.StandardOutput.ReadToEnd();

                if (output.StartsWith("EXCEPTION:") || output.StartsWith("[EXCEPTION]")) {
                    gcinfo.completed = false;
                    gcinfo.time_in_seconds = 999999;
                    gcinfo.exception = new Exception(output);
                } else {
                    gcinfo.completed = true;
                    string[] args = output.Split(',');
                    gcinfo.triangle_count = int.Parse(args[1]);
                    gcinfo.time_in_seconds = int.Parse(args[3]);
                    gcinfo.SliceCount = int.Parse(args[4]);
                    gcinfo.GCodeLines = int.Parse(args[5]);
                    gcinfo.GCodeBytes = int.Parse(args[6]);
                    gcinfo.TotalLength = int.Parse(args[7]);
                }

            } catch (Exception e) {
                System.Console.WriteLine("process-runner exception: " + e.Message);
                gcinfo.completed = false;
                gcinfo.time_in_seconds = 999999;
                gcinfo.exception = new Exception(e.Message);
            }

            return gcinfo;
        }









        static void run_single_process()
        {
            int done_count = 0;
            int MAX_COUNT = 10000;
            bool VERBOSE = false;
            TimeSpan TIMEOUT = TimeSpan.FromSeconds(30);

            int failed_count = 0;

            double MAX_DIM_MM = 50;
            int MAX_TRI_COUNT = 250000;

            HashSet<string> completed =
                File.Exists(CACHE_FILENAME) ? new HashSet<string>(File.ReadAllLines(CACHE_FILENAME)) : new HashSet<string>();

            string[] files = Directory.GetFiles("E:\\Thingi10K\\closed");
            SafeListBuilder<string> result_strings = new SafeListBuilder<string>();
            SafeListBuilder<string> processed_files = new SafeListBuilder<string>();
            gParallel.ForEach(files, (filename) => {

                int i = done_count;
                if (i > MAX_COUNT)
                    return;
                Interlocked.Increment(ref done_count);
                if (i % 10 == 0) {
                    System.Console.WriteLine("started {0} / {1}", i, files.Length);
                }

                if (completed.Contains(filename))
                    return;

                // save progress on this run
                if (i % 10 == 0) {
                    write_output(result_strings);
                    lock(completed) {
                        write_completed(completed, CACHE_FILENAME);
                    }
                }


                DMesh3 mesh = StandardMeshReader.ReadMesh(filename);
                AxisAlignedBox3d bounds = mesh.CachedBounds;
                MeshTransforms.Scale(mesh, MAX_DIM_MM / bounds.MaxDim);
                Vector3d basePt = mesh.CachedBounds.Point(0, 0, -1);
                MeshTransforms.Translate(mesh, -basePt);

                if ( mesh.TriangleCount > MAX_TRI_COUNT) {
                    Reducer r = new Reducer(mesh);
                    r.ReduceToTriangleCount(MAX_TRI_COUNT);
                    mesh = new DMesh3(mesh, true);
                }

                StringBuilder builder = new StringBuilder();
                builder.Append(filename); builder.Append(',');
                builder.Append(mesh.TriangleCount.ToString()); builder.Append(',');

                var start = DateTime.Now;

                if (VERBOSE) System.Console.WriteLine(builder.ToString());
                if (VERBOSE) System.Console.WriteLine(mesh.CachedBounds.ToString());

                GCodeInfo gcinfo = GenerateGCodeForFileWithTimeout2(filename, TIMEOUT);
                if ( gcinfo.exception != null ) { 
                    System.Console.WriteLine(filename + " : " + gcinfo.exception.Message);
                    Interlocked.Increment(ref failed_count);
                }
                if (gcinfo.completed) builder.Append("OK");
                else if (gcinfo.completed == false && gcinfo.timed_out) builder.Append("TIMEOUT");
                else if (gcinfo.completed == false) builder.Append("FAILED");
                builder.Append(',');

                var end = DateTime.Now;
                builder.Append(((int)(end - start).TotalSeconds).ToString()); builder.Append(',');

                builder.Append(gcinfo.SliceCount.ToString());  builder.Append(',');
                builder.Append(gcinfo.GCodeLines.ToString()); builder.Append(',');
                builder.Append(gcinfo.GCodeBytes.ToString()); builder.Append(',');
                builder.Append(gcinfo.TotalLength.ToString()); builder.Append(',');

                if (VERBOSE) System.Console.WriteLine(builder.ToString());
                result_strings.SafeAdd(builder.ToString());

                lock(completed) {
                    completed.Add(filename);
                }
            });

            write_output(result_strings); 
        }

        static void write_output(SafeListBuilder<string> safelist)
        {
            safelist.SafeOperation((list) => {
                list.Sort();
                File.WriteAllLines("../../../sample_output/Thingi10K_slice_results.csv", list);
            });
        }

        static void write_completed(HashSet<string> files, string filename)
        {
            List<string> l = new List<string>(files);
            l.Sort();
            File.WriteAllLines(filename, l);
        }


        static GCodeInfo GenerateGCodeForFileWithTimeout2(string filename, TimeSpan timeout)
        {
            DateTime start = DateTime.Now;

            GCodeInfo gcinfo = new GCodeInfo();
            gcinfo.completed = false;

            bool done = false;
            bool aborted = false;
            Action<string,string> errorF = (msg,stack) => {
                if (aborted == false)
                    gcinfo.exception = new Exception(msg);
            };
            Func<bool> cancelF = () => {
                return aborted;
            };

            Thread t = new Thread(() => {
                try {
                    gcinfo = GenerateGCodeForFile(filename, errorF, cancelF);
                    if (cancelF())
                        gcinfo.timed_out = true;
                    else
                        gcinfo.completed = true;
                } catch (Exception e) {
                    gcinfo.exception = e;
                }
                done = true;
            });
            t.Start();
            while (done == false) {
                Thread.Sleep(1000);
                if ((DateTime.Now - start) > timeout)
                    aborted = true;
            }
            return gcinfo;
        }







        struct GCodeInfo
        {
            public bool completed;
            public bool timed_out;
            public Exception exception;

            public int triangle_count;
            public int time_in_seconds;

            public int SliceCount;
            public AxisAlignedBox3d SliceBounds;

            public int GCodeLines;
            public int GCodeBytes;
            public AxisAlignedBox3d PathBounds;
            public AxisAlignedBox3d ExtrudeBounds;
            public double TotalLength;
        }

        static GCodeInfo GenerateGCodeForFile(string filename, Action<string,string> errorF, Func<bool> cancelF)
        {
            GCodeInfo info = new GCodeInfo();

            DMesh3 mesh = StandardMeshReader.ReadMesh(filename);
            if (mesh == null || mesh.TriangleCount == 0)
                throw new Exception("File " + filename + " is invalid or empty");

            bool ENABLE_SUPPORT_ZSHIFT = true;

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
            slicer.CancelF = cancelF;
            PlanarSliceStack slices = slicer.Compute();
            if (slicer.WasCancelled)
                return info;
            info.SliceCount = slices.Count;
            info.SliceBounds = slices.Bounds;

            // run print generator
            SingleMaterialFFFPrintGenPro printGen =
                new SingleMaterialFFFPrintGenPro(meshes, slices, settings);
            printGen.ErrorF = errorF;
            printGen.CancelF = cancelF;

            if (ENABLE_SUPPORT_ZSHIFT)
                printGen.LayerPostProcessor = new SupportConnectionPostProcessor() { ZOffsetMM = 0.2f };
            printGen.AccumulatePathSet = true;

            printGen.Generate();
            if (printGen.WasCancelled)
                return info;

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

            info.completed = true;

            return info;
        }

    }




    static class AsyncTimeoutExt
    {
        public static async Task<TResult> TimeoutAfter<TResult>(this Task<TResult> task, TimeSpan timeout)
        {

            using (var timeoutCancellationTokenSource = new CancellationTokenSource()) {

                var completedTask = await Task.WhenAny(task, Task.Delay(timeout, timeoutCancellationTokenSource.Token));
                if (completedTask == task) {
                    timeoutCancellationTokenSource.Cancel();
                    return await task;  // Very important in order to propagate exceptions
                } else {
                    throw new TimeoutException("The operation has timed out.");
                }
            }
        }

    }

}
