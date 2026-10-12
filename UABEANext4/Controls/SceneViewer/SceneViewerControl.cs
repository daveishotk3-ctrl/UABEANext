using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using UABEANext4.Logic.Project;

namespace UABEANext4.Controls.SceneViewer;

/// <summary>
/// Shows a <see cref="ProjectScene"/>: every mesh drawn where its object sits in the world.
/// Left drag orbits, right/middle drag (or shift + left drag) pans, the wheel zooms,
/// W/A/S/D/Q/E move and F frames the whole scene.
/// </summary>
public class SceneViewerControl : OpenGlControlBase, ICustomHitTest
{
    private const float FIELD_OF_VIEW = MathF.PI / 4f;
    private const float PITCH_LIMIT = (MathF.PI / 2) - 0.01f;
    // anything that would be smaller than this many pixels on screen isn't worth drawing
    private const float MIN_PIXEL_RADIUS = 0.75f;
    // how long one frame may spend sending new meshes to the GPU, so loading a big scene doesn't freeze the window
    private const long UPLOAD_BUDGET_MS = 8;

    // Unity uses a left handed coordinate system and OpenGL a right handed one.
    // flipping Z gets a picture that looks the same as Unity's, with +X to the right and +Y up.
    private static readonly Matrix4x4 s_unityToDisplay = Matrix4x4.CreateScale(1f, 1f, -1f);

    private struct GpuMesh
    {
        public uint Vao;
        public uint Vbo;
        public uint Ebo;
        public int IndexCount;
        public bool Ready;
    }

    private GL? _gl;
    private uint _vertexShader;
    private uint _fragmentShader;
    private uint _program;
    private int _uModel;
    private int _uViewProjection;
    private int _uNormalMatrix;
    private int _uLightDir;
    private int _uColor;

    private ProjectScene? _scene;
    private GpuMesh[] _gpuMeshes = [];
    private readonly List<GpuMesh[]> _staleGpuMeshes = new();
    private int _uploadCursor;

    // per instance, in the same order as scene.Instances. everything is in display space (Z flipped).
    private Matrix4x4[] _models = [];
    private float[] _normalMatrices = [];
    private Vector3[] _centers = [];
    private float[] _radii = [];
    private Vector3[] _meshColors = [];
    private bool[] _drawable = [];
    // instance indices sorted by mesh, so the GPU only has to switch meshes when needed
    private int[] _drawOrder = [];

    private Vector3 _target = Vector3.Zero;
    private float _yaw = 0.6f;
    private float _pitch = 0.4f;
    private float _distance = 15f;
    private float _sceneRadius = 10f;

    private Vector2 _lastPointerPos;
    private bool _dragging;
    private int _framesRequested = 4;

    public SceneViewerControl()
    {
        Focusable = true;

        PointerPressed += SceneViewer_PointerPressed;
        PointerReleased += SceneViewer_PointerReleased;
        PointerMoved += SceneViewer_PointerMoved;
        PointerWheelChanged += SceneViewer_PointerWheelChanged;
        KeyDown += SceneViewer_KeyDown;
    }

    public static readonly DirectProperty<SceneViewerControl, ProjectScene?> SceneProperty =
        AvaloniaProperty.RegisterDirect<SceneViewerControl, ProjectScene?>(
            nameof(Scene), o => o.Scene, (o, v) => o.Scene = v);

    public static readonly StyledProperty<bool> ShowInactiveProperty =
        AvaloniaProperty.Register<SceneViewerControl, bool>(nameof(ShowInactive), true);

    public ProjectScene? Scene
    {
        get => _scene;
        set
        {
            if (SetAndRaise(SceneProperty, ref _scene, value))
            {
                ApplyScene();
            }
        }
    }

    /// <summary>
    /// Also draw objects that are switched off in the project (inactive objects and disabled renderers).
    /// </summary>
    public bool ShowInactive
    {
        get => GetValue(ShowInactiveProperty);
        set => SetValue(ShowInactiveProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ShowInactiveProperty || change.Property == BoundsProperty)
        {
            RequestFrames();
        }
    }

    public bool HitTest(Point point)
    {
        return true;
    }

    /// <summary>
    /// Moves the camera so the whole scene fits in view.
    /// </summary>
    public void FrameAll()
    {
        if (_scene == null || !_scene.HasBounds)
        {
            _target = Vector3.Zero;
            _sceneRadius = 10f;
            _distance = 15f;
        }
        else
        {
            var min = _scene.BoundsMin;
            var max = _scene.BoundsMax;
            var center = (min + max) * 0.5f;

            _target = Vector3.Transform(center, s_unityToDisplay);
            _sceneRadius = MathF.Max((max - min).Length() * 0.5f, 0.01f);
            _distance = _sceneRadius / MathF.Sin(FIELD_OF_VIEW * 0.5f) * 1.05f;
        }

        RequestFrames();
    }

    private void RequestFrames()
    {
        _framesRequested = 4;

        // before OpenGL starts, Avalonia draws the first frame by itself once the control is shown
        if (_gl != null)
        {
            RequestNextFrameRendering();
        }
    }

    // ///////////// scene setup

    private void ApplyScene()
    {
        // GPU buffers can only be deleted on the GPU thread, so hand the old ones over to the next frame
        if (_gpuMeshes.Length > 0)
        {
            _staleGpuMeshes.Add(_gpuMeshes);
        }

        _uploadCursor = 0;
        var scene = _scene;
        if (scene == null)
        {
            _gpuMeshes = [];
            _models = [];
            _normalMatrices = [];
            _centers = [];
            _radii = [];
            _meshColors = [];
            _drawable = [];
            _drawOrder = [];
            RequestFrames();
            return;
        }

        _gpuMeshes = new GpuMesh[scene.Meshes.Count];

        _meshColors = new Vector3[scene.Meshes.Count];
        for (var i = 0; i < _meshColors.Length; i++)
        {
            _meshColors[i] = GetMeshColor(i);
        }

        var count = scene.Instances.Count;
        _models = new Matrix4x4[count];
        _normalMatrices = new float[count * 9];
        _centers = new Vector3[count];
        _radii = new float[count];
        _drawable = new bool[count];

        var sortKeys = new int[count];
        _drawOrder = new int[count];

        for (var i = 0; i < count; i++)
        {
            var instance = scene.Instances[i];
            var mesh = scene.Meshes[instance.MeshIndex];
            var model = instance.World * s_unityToDisplay;

            _models[i] = model;
            _drawOrder[i] = i;
            sortKeys[i] = instance.MeshIndex;

            // an object squashed flat (scale 0) can't be drawn and has no normal matrix
            if (!Matrix4x4.Invert(model, out var inverse) || !IsFinite(model))
                continue;

            // normals use the inverse of the model matrix. System.Numerics matrices are row vector style,
            // and the shader uploads this one transposed, so the rows go in as they are.
            var n = i * 9;
            _normalMatrices[n + 0] = inverse.M11;
            _normalMatrices[n + 1] = inverse.M12;
            _normalMatrices[n + 2] = inverse.M13;
            _normalMatrices[n + 3] = inverse.M21;
            _normalMatrices[n + 4] = inverse.M22;
            _normalMatrices[n + 5] = inverse.M23;
            _normalMatrices[n + 6] = inverse.M31;
            _normalMatrices[n + 7] = inverse.M32;
            _normalMatrices[n + 8] = inverse.M33;

            // a sphere around the mesh, for skipping what is off screen
            var localCenter = (mesh.BoundsMin + mesh.BoundsMax) * 0.5f;
            var half = (mesh.BoundsMax - mesh.BoundsMin) * 0.5f;
            var scaleX = new Vector3(model.M11, model.M12, model.M13).Length();
            var scaleY = new Vector3(model.M21, model.M22, model.M23).Length();
            var scaleZ = new Vector3(model.M31, model.M32, model.M33).Length();
            var scaledHalf = new Vector3(half.X * scaleX, half.Y * scaleY, half.Z * scaleZ);

            _centers[i] = Vector3.Transform(localCenter, model);
            _radii[i] = scaledHalf.Length();
            _drawable[i] = float.IsFinite(_radii[i]) && IsFinite(_centers[i]);
        }

        Array.Sort(sortKeys, _drawOrder);

        FrameAll();
    }

    private static bool IsFinite(Vector3 v)
    {
        return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }

    private static bool IsFinite(Matrix4x4 m)
    {
        return float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
            && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
            && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
            && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
    }

    /// <summary>
    /// Spreads colors around the color wheel using the golden ratio, so neighbouring meshes look different.
    /// </summary>
    private static Vector3 GetMeshColor(int meshIndex)
    {
        var hue = (meshIndex * 0.61803398875f) % 1f;
        const float saturation = 0.35f;
        const float value = 0.95f;

        var h = hue * 6f;
        var sector = (int)MathF.Floor(h);
        var f = h - sector;
        var p = value * (1f - saturation);
        var q = value * (1f - saturation * f);
        var t = value * (1f - saturation * (1f - f));

        return (sector % 6) switch
        {
            0 => new Vector3(value, t, p),
            1 => new Vector3(q, value, p),
            2 => new Vector3(p, value, t),
            3 => new Vector3(p, q, value),
            4 => new Vector3(t, p, value),
            _ => new Vector3(value, p, q)
        };
    }

    // ///////////// input

    private void SceneViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed && !point.Properties.IsMiddleButtonPressed)
            return;

        var pos = e.GetPosition(this);
        _lastPointerPos = new Vector2((float)pos.X, (float)pos.Y);
        _dragging = true;
        e.Pointer.Capture(this);
    }

    private void SceneViewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed && !props.IsMiddleButtonPressed)
        {
            _dragging = false;
            e.Pointer.Capture(null);
        }
    }

    private void SceneViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;

        var props = e.GetCurrentPoint(this).Properties;
        var pos = e.GetPosition(this);
        var current = new Vector2((float)pos.X, (float)pos.Y);
        var delta = current - _lastPointerPos;
        _lastPointerPos = current;

        var pan = props.IsRightButtonPressed
            || props.IsMiddleButtonPressed
            || (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift));

        if (pan)
        {
            GetCameraAxes(out _, out var right, out var up);
            var scale = _distance * 0.0016f;
            _target += (-delta.X * right + delta.Y * up) * scale;
        }
        else if (props.IsLeftButtonPressed)
        {
            _yaw -= delta.X * 0.006f;
            _pitch += delta.Y * 0.006f;
            _pitch = MathF.Max(MathF.Min(_pitch, PITCH_LIMIT), -PITCH_LIMIT);
        }
        else
        {
            return;
        }

        RequestFrames();
    }

    private void SceneViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _distance *= MathF.Pow(0.88f, (float)e.Delta.Y);
        _distance = MathF.Max(_distance, MathF.Max(_sceneRadius * 0.0005f, 0.001f));
        RequestFrames();
    }

    private void SceneViewer_KeyDown(object? sender, KeyEventArgs e)
    {
        var step = _distance * 0.05f;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            step *= 3f;

        GetCameraAxes(out var forward, out var right, out _);

        switch (e.Key)
        {
            case Key.W: _target += forward * step; break;
            case Key.S: _target -= forward * step; break;
            case Key.D: _target += right * step; break;
            case Key.A: _target -= right * step; break;
            case Key.E: _target += Vector3.UnitY * step; break;
            case Key.Q: _target -= Vector3.UnitY * step; break;
            case Key.F: FrameAll(); e.Handled = true; return;
            default: return;
        }

        e.Handled = true;
        RequestFrames();
    }

    private Vector3 GetEyePosition()
    {
        return _target + _distance * new Vector3(
            MathF.Cos(_pitch) * MathF.Sin(_yaw),
            MathF.Sin(_pitch),
            MathF.Cos(_pitch) * MathF.Cos(_yaw));
    }

    private void GetCameraAxes(out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        forward = Vector3.Normalize(_target - GetEyePosition());
        right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        up = Vector3.Cross(right, forward);
    }

    // ///////////// OpenGL

    private static uint LoadShader(GL gl, ShaderType shaderType, string content)
    {
        var shader = gl.CreateShader(shaderType);
        gl.ShaderSource(shader, content);
        gl.CompileShader(shader);

        var infoLog = gl.GetShaderInfoLog(shader);
        if (!string.IsNullOrWhiteSpace(infoLog))
        {
            Debug.WriteLine($"Scene viewer {shaderType} log: {infoLog}");
        }

        return shader;
    }

    protected override unsafe void OnOpenGlInit(GlInterface glInterface)
    {
        base.OnOpenGlInit(glInterface);

        // this runs again when the control is shown after being hidden (switching dock tabs, for example),
        // and the OpenGL objects from last time are gone by then
        var gl = GL.GetApi(glInterface.GetProcAddress);
        _gl = gl;

        Debug.WriteLine($"Renderer: {gl.GetStringS(GLEnum.Renderer)} Version: {gl.GetStringS(GLEnum.Version)}");

        _vertexShader = LoadShader(gl, ShaderType.VertexShader, SceneViewerShaders.VERTEX_SOURCE);
        _fragmentShader = LoadShader(gl, ShaderType.FragmentShader, SceneViewerShaders.FRAGMENT_SOURCE);

        _program = gl.CreateProgram();
        gl.AttachShader(_program, _vertexShader);
        gl.AttachShader(_program, _fragmentShader);
        gl.BindAttribLocation(_program, SceneViewerShaders.POSITION_LOC, "aPos");
        gl.BindAttribLocation(_program, SceneViewerShaders.NORMAL_LOC, "aNormal");
        gl.LinkProgram(_program);

        var programLog = gl.GetProgramInfoLog(_program);
        if (!string.IsNullOrWhiteSpace(programLog))
        {
            Debug.WriteLine($"Scene viewer program log: {programLog}");
        }

        _uModel = gl.GetUniformLocation(_program, "uModel");
        _uViewProjection = gl.GetUniformLocation(_program, "uViewProjection");
        _uNormalMatrix = gl.GetUniformLocation(_program, "uNormalMatrix");
        _uLightDir = gl.GetUniformLocation(_program, "uLightDir");
        _uColor = gl.GetUniformLocation(_program, "uColor");

        // start over: nothing is on the GPU yet
        _staleGpuMeshes.Clear();
        _uploadCursor = 0;
        if (_scene != null)
        {
            _gpuMeshes = new GpuMesh[_scene.Meshes.Count];
        }

        _framesRequested = 4;
    }

    private unsafe void UploadMesh(GL gl, int index)
    {
        var mesh = _scene!.Meshes[index];
        if (mesh.Vertices.Length == 0 || mesh.Indices.Length == 0)
            return;

        const uint stride = 6 * sizeof(float);

        var vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);

        var vbo = gl.GenBuffer();
        gl.BindBuffer(GLEnum.ArrayBuffer, vbo);
        fixed (void* data = mesh.Vertices)
        {
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(mesh.Vertices.Length * sizeof(float)), data, BufferUsageARB.StaticDraw);
        }

        gl.VertexAttribPointer(SceneViewerShaders.POSITION_LOC, 3, GLEnum.Float, false, stride, (void*)0);
        gl.VertexAttribPointer(SceneViewerShaders.NORMAL_LOC, 3, GLEnum.Float, false, stride, (void*)12);
        gl.EnableVertexAttribArray(SceneViewerShaders.POSITION_LOC);
        gl.EnableVertexAttribArray(SceneViewerShaders.NORMAL_LOC);

        // the index buffer binding is saved in the vertex array, so it is bound last and left bound
        var ebo = gl.GenBuffer();
        gl.BindBuffer(GLEnum.ElementArrayBuffer, ebo);
        fixed (void* data = mesh.Indices)
        {
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(mesh.Indices.Length * sizeof(uint)), data, BufferUsageARB.StaticDraw);
        }

        gl.BindVertexArray(0);
        gl.BindBuffer(GLEnum.ArrayBuffer, 0);

        _gpuMeshes[index] = new GpuMesh
        {
            Vao = vao,
            Vbo = vbo,
            Ebo = ebo,
            IndexCount = mesh.Indices.Length,
            Ready = true
        };
    }

    private static void DeleteGpuMeshes(GL gl, GpuMesh[] meshes)
    {
        for (var i = 0; i < meshes.Length; i++)
        {
            if (!meshes[i].Ready)
                continue;

            gl.DeleteVertexArray(meshes[i].Vao);
            gl.DeleteBuffer(meshes[i].Vbo);
            gl.DeleteBuffer(meshes[i].Ebo);
            meshes[i].Ready = false;
        }
    }

    /// <summary>
    /// Sends meshes to the GPU until the time budget for this frame runs out.
    /// </summary>
    /// <returns>True if there are still meshes left to send.</returns>
    private bool UploadPendingMeshes(GL gl)
    {
        if (_scene == null)
            return false;

        var timer = Stopwatch.StartNew();
        while (_uploadCursor < _scene.Meshes.Count)
        {
            UploadMesh(gl, _uploadCursor);
            _uploadCursor++;

            if (timer.ElapsedMilliseconds > UPLOAD_BUDGET_MS)
                break;
        }

        return _uploadCursor < _scene.Meshes.Count;
    }

    private static void GetSidePlanes(in Matrix4x4 viewProjection, Span<Vector4> planes)
    {
        // a point is in front of a plane when dot(plane.xyz, point) + plane.w >= 0.
        // with row vector matrices, clip.w and clip.x/y are the 4th, 1st and 2nd columns.
        var m = viewProjection;
        planes[0] = new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41); // left
        planes[1] = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41); // right
        planes[2] = new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42); // bottom
        planes[3] = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42); // top

        for (var i = 0; i < 4; i++)
        {
            var length = new Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length();
            if (length > 1e-6f)
            {
                planes[i] /= length;
            }
        }
    }

    protected override unsafe void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        var gl = _gl ??= GL.GetApi(glInterface.GetProcAddress);

        foreach (var stale in _staleGpuMeshes)
        {
            DeleteGpuMeshes(gl, stale);
        }
        _staleGpuMeshes.Clear();

        var uploadsPending = UploadPendingMeshes(gl);

        // Bounds are in device independent units, but the OpenGL surface is in real pixels
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max((int)(Bounds.Width * scaling), 1);
        var height = Math.Max((int)(Bounds.Height * scaling), 1);

        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.ClearColor(0.13f, 0.14f, 0.16f, 1f);
        gl.Clear((uint)(GLEnum.ColorBufferBit | GLEnum.DepthBufferBit));
        gl.Enable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);

        if (_scene != null && _scene.Instances.Count > 0)
        {
            DrawScene(gl, width, height);
        }

        _framesRequested = Math.Max(_framesRequested - 1, 0);
        if (_framesRequested > 0 || uploadsPending)
        {
            RequestNextFrameRendering();
        }
    }

    private unsafe void DrawScene(GL gl, int width, int height)
    {
        var scene = _scene!;

        var eye = GetEyePosition();
        var near = MathF.Max(_distance * 0.01f, 0.001f);
        var far = _distance + _sceneRadius * 2f + near * 10f;

        var view = Matrix4x4.CreateLookAt(eye, _target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FIELD_OF_VIEW, (float)width / height, near, far);
        var viewProjection = view * projection;

        Span<Vector4> planes = stackalloc Vector4[4];
        GetSidePlanes(viewProjection, planes);

        // how many pixels a sphere of radius 1 at distance 1 covers
        var pixelsPerUnit = height * 0.5f / MathF.Tan(FIELD_OF_VIEW * 0.5f);

        gl.UseProgram(_program);
        gl.UniformMatrix4(_uViewProjection, 1, false, &viewProjection.M11);
        gl.Uniform3(_uLightDir, -0.5f, -1.0f, -0.35f);

        var showInactive = ShowInactive;
        var lastMesh = -1;

        for (var k = 0; k < _drawOrder.Length; k++)
        {
            var i = _drawOrder[k];
            if (!_drawable[i])
                continue;

            var instance = scene.Instances[i];
            if (!showInactive && (!instance.ActiveInHierarchy || !instance.RendererEnabled))
                continue;

            var gpuMesh = _gpuMeshes[instance.MeshIndex];
            if (!gpuMesh.Ready)
                continue;

            var center = _centers[i];
            var radius = _radii[i];

            // off to the side of the camera
            var culled = false;
            for (var p = 0; p < 4; p++)
            {
                var plane = planes[p];
                if (plane.X * center.X + plane.Y * center.Y + plane.Z * center.Z + plane.W < -radius)
                {
                    culled = true;
                    break;
                }
            }
            if (culled)
                continue;

            // too far away to see
            var distanceToEye = MathF.Max(Vector3.Distance(center, eye), 1e-4f);
            if (radius / distanceToEye * pixelsPerUnit < MIN_PIXEL_RADIUS)
                continue;

            if (instance.MeshIndex != lastMesh)
            {
                gl.BindVertexArray(gpuMesh.Vao);
                var color = _meshColors[instance.MeshIndex];
                gl.Uniform3(_uColor, color.X, color.Y, color.Z);
                lastMesh = instance.MeshIndex;
            }

            var model = _models[i];
            gl.UniformMatrix4(_uModel, 1, false, &model.M11);
            fixed (float* normalMatrix = &_normalMatrices[i * 9])
            {
                gl.UniformMatrix3(_uNormalMatrix, 1, true, normalMatrix);
            }

            gl.DrawElements(PrimitiveType.Triangles, (uint)gpuMesh.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }

        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    protected override void OnOpenGlDeinit(GlInterface glInterface)
    {
        var gl = _gl ?? GL.GetApi(glInterface.GetProcAddress);

        gl.BindVertexArray(0);
        gl.UseProgram(0);

        DeleteGpuMeshes(gl, _gpuMeshes);
        foreach (var stale in _staleGpuMeshes)
        {
            DeleteGpuMeshes(gl, stale);
        }
        _staleGpuMeshes.Clear();

        gl.DeleteProgram(_program);
        gl.DeleteShader(_fragmentShader);
        gl.DeleteShader(_vertexShader);

        _program = 0;
        _gl = null;
        base.OnOpenGlDeinit(glInterface);
    }
}
