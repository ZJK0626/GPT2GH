﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace GPT2GH
{
    public class ExportGCodeComponent : GH_Component
    {
        /// <summary>
        /// 构造函数：设定组件名称、昵称、描述、所属类别/子类别
        /// </summary>
        public ExportGCodeComponent()
          : base("Export GCode",               // 组件名称
                 "GCode",                      // GH 中简短昵称
                 "Export lines as simple G-code",
                 "GPT2GH",                         // GH 一级标签 (同之前 SlicingBrepComponent)
                 "Processing"                  // GH 二级标签
                )
        {
        }

        /// <summary>
        /// 在这里定义输入参数
        /// </summary>
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 0: 线段集合
            pManager.AddLineParameter(
                "Lines", "L",
                "The line segments to export as G-code motion",
                GH_ParamAccess.list);

            // 1: 输出 G-code 文件路径
            pManager.AddTextParameter(
                "FilePath", "F",
                "Where to save the G-code file (e.g. D:\\test.gcode)",
                GH_ParamAccess.item,
                "D:\\test.gcode");

            // 2: 进给速度
            pManager.AddNumberParameter(
                "FeedRate", "Fr",
                "G-code feed rate (mm/min)",
                GH_ParamAccess.item,
                1000.0);
        }

        /// <summary>
        /// 注册输出参数
        /// </summary>
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 0: 预览 G-code 文本
            pManager.AddTextParameter(
                "GCode", "G",
                "Preview of the generated G-code text",
                GH_ParamAccess.item);
        }

        /// <summary>
        /// 核心处理逻辑
        /// </summary>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // 1. 获取输入
            var lines = new List<Line>();
            string filePath = "";
            double feedRate = 1000.0;

            if (!DA.GetDataList(0, lines)) return;
            if (!DA.GetData(1, ref filePath)) return;
            if (!DA.GetData(2, ref feedRate)) return;

            // 2. 生成 G-code 文本
            string gcodeText = GenerateGCodeFromLines(lines, feedRate);

            // 3. 写出到指定文件
            try
            {
                File.WriteAllText(filePath, gcodeText);
            }
            catch (Exception ex)
            {
                this.AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Failed to write G-code file: " + ex.Message);
            }

            DA.SetData(0, gcodeText);
        }

        /// <summary>
        /// 将若干线段转换为最基础的 3轴 G-code (仅包含直线移动指令)
        /// </summary>
        private string GenerateGCodeFromLines(List<Line> lines, double feedRate)
        {
            var sb = new StringBuilder();

            // -- G-code 头部指令 --
            sb.AppendLine("; G-code generated by MyGrasshopperPlugin");
            sb.AppendLine("G90");     // 绝对坐标模式
            sb.AppendLine("G21");     // 单位：毫米
            sb.AppendLine($"F{feedRate:F2}");
            sb.AppendLine();

            // -- 对每条线段输出移动轨迹 --
            foreach (var ln in lines)
            {
                Point3d start = ln.From;
                Point3d end = ln.To;

                // 先快速移动 (G0) 到起点
                sb.AppendLine(
                    $"G0 X{start.X:F3} Y{start.Y:F3} Z{start.Z:F3}");

                // 再线性插补 (G1) 到终点
                sb.AppendLine(
                    $"G1 X{end.X:F3} Y{end.Y:F3} Z{end.Z:F3}");

                sb.AppendLine(); // 空行分隔
            }

            // -- G-code 结束指令 --
            sb.AppendLine("M2 ; End of program"); // 或者M30等
            return sb.ToString();
        }

        /// <summary>
        /// 保持和之前示例类似的 GUID
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("f210c610-67c4-4027-9bfb-f959a9665e7b"); }
        }

        // 如果有自定义图标，可在此返回；没有就返回null
        protected override System.Drawing.Bitmap Icon
        {
            get { return null; }
        }
    }
}
