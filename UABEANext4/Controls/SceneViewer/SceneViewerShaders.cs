namespace UABEANext4.Controls.SceneViewer;
public static class SceneViewerShaders
{
    // The model matrix places the mesh in the world and uViewProjection is view * projection.
    // The normal matrix is worked out once per object on the CPU, so the vertex shader doesn't
    // have to invert a matrix for every vertex.
    public const string VERTEX_SOURCE = @"#version 300 es
precision highp float;
layout (location = 0) in vec3 aPos;
layout (location = 1) in vec3 aNormal;
uniform mat4 uModel;
uniform mat4 uViewProjection;
uniform mat3 uNormalMatrix;
out vec3 vNormal;
void main()
{
    gl_Position = uViewProjection * uModel * vec4(aPos, 1.0);
    vNormal = uNormalMatrix * aNormal;
}";

    // meshes are drawn without culling, so surfaces seen from behind (planes, leaves...) still show up.
    // there are no materials yet, so every mesh gets its own flat color to tell them apart.
    public const string FRAGMENT_SOURCE = @"#version 300 es
precision highp float;
in vec3 vNormal;
out vec4 FragColor;
uniform vec3 uLightDir;
uniform vec3 uColor;
void main()
{
    vec3 n = normalize(vNormal);
    float ndl = dot(n, -normalize(uLightDir));
    float diffuse = max(ndl, 0.0) * 0.65 + max(-ndl, 0.0) * 0.15;
    float sky = 0.5 + 0.5 * n.y;
    vec3 lit = uColor * (0.22 + 0.25 * sky + diffuse);
    FragColor = vec4(lit, 1.0);
}";

    public const int POSITION_LOC = 0;
    public const int NORMAL_LOC = 1;
}
