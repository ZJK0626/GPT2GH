﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace GPT2GH
{
    public class TrellisComponent : GH_Component
    {
        // Process tracking
        private Process _process = null;
        private bool _isGenerating = false;
        private string _lastOutputDir = null;
        private string _statusMessage = "Ready";

        // Log storage
        private string _logOutput = "";

        public TrellisComponent()
          : base("TRELLIS 3D Generator",
                 "TRELLIS",
                 "Generate 3D models from images or text using the TRELLIS model",
                 "GPT2GH",
                 "Text2Mesh")
        {
        }

        public override Guid ComponentGuid => new Guid("7A2B8D3E-4F5C-40A7-B2C1-DEFAB1235678");

 
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Mode", "M", "Generation mode: 'image' or 'text'", GH_ParamAccess.item, "image");
            pManager.AddTextParameter("Input", "Input", "For image mode: path to the input image file. For text mode: text prompt", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Seed", "S", "Random seed for generation", GH_ParamAccess.item, 0);
            pManager.AddTextParameter("Output Directory", "OutPath", "Directory to save generated files", GH_ParamAccess.item);

            // Advanced parameters with defaults
            pManager.AddIntegerParameter("SS Steps", "SS", "Sampling steps for sparse structure generation", GH_ParamAccess.item, 12);
            pManager.AddNumberParameter("SS Guidance", "SG", "Guidance strength for sparse structure generation", GH_ParamAccess.item, 7.5);
            pManager.AddIntegerParameter("SLat Steps", "LS", "Sampling steps for structured latent generation", GH_ParamAccess.item, 12);
            pManager.AddNumberParameter("SLat Guidance", "LG", "Guidance strength for structured latent generation", GH_ParamAccess.item, 3.0);

            pManager.AddTextParameter("Batch Script", "B", "Path to the TRELLIS batch script", GH_ParamAccess.item, @"C:\Users\DELL\TRELLIS\run_trellis.bat");
            pManager.AddBooleanParameter("Generate", "Run", "Set to true to start generation", GH_ParamAccess.item, false);

            // Hide advanced parameters by default
            for (int i = 4; i < 9; i++)
            {
                pManager[i].Optional = true;
            }
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Mesh", "M", "Generated 3D mesh", GH_ParamAccess.item);
            pManager.AddTextParameter("Status", "S", "Generation status information", GH_ParamAccess.item);
            pManager.AddTextParameter("Log", "L", "Process output log", GH_ParamAccess.item);
            pManager.AddTextParameter("GLB Path", "GLB", "Path to the GLB file with textures", GH_ParamAccess.item);
            pManager.AddTextParameter("PLY Path", "PLY", "Path to the Gaussian PLY file", GH_ParamAccess.item);
            pManager.AddTextParameter("Preview", "P", "Path to the preview video", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // Get input parameters
            string mode = "image";
            string input = "";
            int seed = 0;
            string outputDir = Path.Combine(Path.GetTempPath(), "TRELLIS_" + Guid.NewGuid().ToString("N"));
            int ssSteps = 12;
            double ssGuidance = 7.5;
            int slatSteps = 12;
            double slatGuidance = 3.0;
            string batchScriptPath = @"C:\Users\DELL\TRELLIS\run_trellis.bat";
            bool generate = false;

            if (!DA.GetData(0, ref mode)) return;
            if (!DA.GetData(1, ref input)) return;
            DA.GetData(2, ref seed);
            DA.GetData(3, ref outputDir);
            DA.GetData(4, ref ssSteps);
            DA.GetData(5, ref ssGuidance);
            DA.GetData(6, ref slatSteps);
            DA.GetData(7, ref slatGuidance);
            DA.GetData(8, ref batchScriptPath);
            if (!DA.GetData(9, ref generate)) return;

            // Validate inputs
            mode = mode.ToLower().Trim();
            if (mode != "image" && mode != "text")
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Mode must be either 'image' or 'text'");
                return;
            }

            if (string.IsNullOrEmpty(input))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is required");
                return;
            }

            if (mode == "image" && !File.Exists(input))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Input image file not found: {input}");
                return;
            }

            if (string.IsNullOrEmpty(outputDir))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Output directory is required");
                return;
            }

            // Check if batch script path exists
            if (string.IsNullOrEmpty(batchScriptPath) || !File.Exists(batchScriptPath))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Batch script not found at: {batchScriptPath}");
                return;
            }

            // Create output directory if it doesn't exist
            try
            {
                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Failed to create output directory: {ex.Message}");
                return;
            }

            Mesh resultMesh = null;
            string glbPath = null;
            string plyPath = null;
            string previewPath = null;

            // Check if we need to generate or if we already have results
            bool shouldGenerate = generate && !_isGenerating;
            bool resultsExist = Directory.Exists(outputDir) &&
                               File.Exists(Path.Combine(outputDir, "model.obj")) &&
                               outputDir == _lastOutputDir;

            // If we're not generating and results exist, just load them
            if (!shouldGenerate && resultsExist)
            {
                string objPath = Path.Combine(outputDir, "model.obj");
                glbPath = Path.Combine(outputDir, "model.glb");
                plyPath = Path.Combine(outputDir, "gaussian.ply");
                previewPath = Path.Combine(outputDir, "preview.mp4");

                if (File.Exists(objPath))
                {
                    resultMesh = ImportObj(objPath);
                    _statusMessage = "Loaded existing model";
                }
            }
            // Otherwise, if we need to generate, start the process
            else if (shouldGenerate)
            {
                _isGenerating = true;
                _lastOutputDir = outputDir;
                _statusMessage = "Starting TRELLIS generation...";
                _logOutput = ""; // Clear previous log

                // Start generation in a separate task
                Task.Run(() =>
                {
                    try
                    {
                        RunTrellisProcess(mode, input, outputDir, seed, ssSteps, ssGuidance, slatSteps, slatGuidance, batchScriptPath);

                        // Check if the output files were created
                        string objPath = Path.Combine(outputDir, "model.obj");
                        if (File.Exists(objPath))
                        {
                            _statusMessage = "Generation completed successfully";
                        }
                        else
                        {
                            _statusMessage = "Generation process completed but output files not found";
                        }

                        // Trigger solution update when done
                        Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
                        {
                            _isGenerating = false;
                            ExpireSolution(true);
                        }));
                    }
                    catch (Exception ex)
                    {
                        Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
                        {
                            _isGenerating = false;
                            _statusMessage = $"Error: {ex.Message}";
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "TRELLIS generation failed: " + ex.Message);
                            ExpireSolution(true);
                        }));
                    }
                });
            }
            else if (_isGenerating)
            {
                // Still generating
            }

            // Set outputs
            DA.SetData(0, resultMesh);
            DA.SetData(1, _statusMessage);
            DA.SetData(2, _logOutput);
            DA.SetData(3, glbPath);
            DA.SetData(4, plyPath);
            DA.SetData(5, previewPath);
        }

        private void RunTrellisProcess(
            string mode,
            string input,
            string outputDir,
            int seed,
            int ssSteps,
            double ssGuidance,
            int slatSteps,
            double slatGuidance,
            string batchScriptPath)
        {
            string arguments =
                $"--mode {mode} " +
                $"--input \"{input}\" " +
                $"--output_dir \"{outputDir}\" " +
                $"--seed {seed} " +
                $"--ss_steps {ssSteps} " +
                $"--ss_guidance {ssGuidance} " +
                $"--slat_steps {slatSteps} " +
                $"--slat_guidance {slatGuidance}";

            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = batchScriptPath,
                Arguments = arguments,
                UseShellExecute = false,         // Capture output
                CreateNoWindow = false,         // Show the window
                RedirectStandardOutput = true,   // Capture output
                RedirectStandardError = true,    // Capture errors
                WorkingDirectory = Path.GetDirectoryName(batchScriptPath)
            };

            using (Process process = new Process())
            {
                process.StartInfo = psi;
                _process = process;

                // Set up output handling
                StringBuilder output = new StringBuilder();
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        output.AppendLine(e.Data);
                        // Update status message based on output
                        if (e.Data.Contains("SUCCESS:"))
                        {
                            _statusMessage = "Generation completed successfully";
                        }
                        else if (e.Data.Contains("ERROR:"))
                        {
                            _statusMessage = e.Data;
                        }
                        else if (e.Data.Contains("Attempting to import TRELLIS modules"))
                        {
                            _statusMessage = "Importing TRELLIS modules...";
                        }
                        else if (e.Data.Contains("TRELLIS modules imported successfully"))
                        {
                            _statusMessage = "Starting generation...";
                        }
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        output.AppendLine("ERROR: " + e.Data);
                    }
                };

                // Start the process
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for completion
                process.WaitForExit();

                // Store output for display
                _logOutput = output.ToString();

                // Check exit code
                if (process.ExitCode != 0)
                {
                    throw new Exception($"TRELLIS generation failed with exit code {process.ExitCode}. See log for details.");
                }

                _process = null;
            }
        }

        private Mesh ImportObj(string path)
        {
            try
            {
                // Simple manual OBJ parser
                Mesh mesh = new Mesh();
                string[] lines = File.ReadAllLines(path);
                List<Point3d> vertices = new List<Point3d>();

                // First pass: collect all vertices
                foreach (string line in lines)
                {
                    if (line.StartsWith("v "))
                    {
                        string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            double x, y, z;
                            if (double.TryParse(parts[1], out x) &&
                                double.TryParse(parts[2], out y) &&
                                double.TryParse(parts[3], out z))
                            {
                                vertices.Add(new Point3d(x, y, z));
                            }
                        }
                    }
                }

                // Add vertices to mesh
                foreach (Point3d v in vertices)
                {
                    mesh.Vertices.Add(v);
                }

                // Second pass: collect all faces
                foreach (string line in lines)
                {
                    if (line.StartsWith("f "))
                    {
                        string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4) // At least a triangle
                        {
                            try
                            {
                                // Extract vertex indices, ignoring any texture/normal info
                                int[] indices = new int[parts.Length - 1];
                                for (int i = 1; i < parts.Length; i++)
                                {
                                    string indexPart = parts[i].Split('/')[0];
                                    int idx;
                                    if (int.TryParse(indexPart, out idx))
                                    {
                                        indices[i - 1] = idx - 1; // OBJ indices are 1-based
                                    }
                                }

                                // Validate indices
                                bool validIndices = true;
                                foreach (int idx in indices)
                                {
                                    if (idx < 0 || idx >= mesh.Vertices.Count)
                                    {
                                        validIndices = false;
                                        break;
                                    }
                                }

                                if (!validIndices) continue;

                                // Add face - handle both triangles and quads
                                if (indices.Length == 3)
                                {
                                    mesh.Faces.AddFace(indices[0], indices[1], indices[2]);
                                }
                                else if (indices.Length == 4)
                                {
                                    mesh.Faces.AddFace(indices[0], indices[1], indices[2], indices[3]);
                                }
                                else if (indices.Length > 4)
                                {
                                    // Triangulate n-gons
                                    for (int i = 1; i < indices.Length - 1; i++)
                                    {
                                        mesh.Faces.AddFace(indices[0], indices[i], indices[i + 1]);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // Log but continue with other faces
                                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                                    $"Error parsing face: {line}. {ex.Message}");
                                continue;
                            }
                        }
                    }
                }

                // Validate and clean up the mesh
                if (mesh.Vertices.Count > 0 && mesh.Faces.Count > 0)
                {
                    mesh.Normals.ComputeNormals();
                    mesh.Compact();

                    // Apply transformations
                    // 1. First, rotate 90 degrees around X axis to match Rhino's coordinate system (existing)
                    mesh.Rotate(Math.PI / 2, Vector3d.XAxis, Point3d.Origin);

                    // 2. Scale the mesh by a factor of 80
                    EnlargeMesh(mesh, 50.0);

                    return mesh;
                }
                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "OBJ file produced an empty mesh. Check file format.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Failed to import OBJ: {ex.Message}");
                return null;
            }
        }

      

        // Function 2: Enlarge the mesh by a factor
        private void EnlargeMesh(Mesh mesh, double scaleFactor)
        {
            if (mesh == null || mesh.Vertices.Count == 0)
                return;

            // Calculate the center of the mesh (for scaling around its center)
            BoundingBox bbox = mesh.GetBoundingBox(false);
            Point3d center = bbox.Center;

            // Create a scaling transformation
            Transform scaling = Transform.Scale(center, scaleFactor);

            // Apply the transformation to the mesh
            mesh.Transform(scaling);
        }


        public override void RemovedFromDocument(GH_Document document)
        {
            base.RemovedFromDocument(document);
            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill();
                }
                catch
                {
                    // Ignore errors during process kill
                }
            }
        }
    }
}