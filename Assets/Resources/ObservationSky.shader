Shader "Taipei/Observation Sky"
{
    Properties
    {
        _Horizon ("Horizon", Color) = (.69,.77,.81,1)
        _Zenith ("Zenith", Color) = (.26,.51,.72,1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "UnityCG.cginc"

            struct Varyings
            {
                float4 position : SV_POSITION;
                float3 direction : TEXCOORD0;
            };

            float4 _Horizon, _Zenith;

            Varyings Vertex(float4 position : POSITION)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(position);
                output.direction = position.xyz;
                return output;
            }

            float4 Fragment(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.direction);
                float altitude = saturate(direction.y * 1.8);
                float3 sky = lerp(_Horizon.rgb, _Zenith.rgb, sqrt(altitude));
                float sunward = saturate(dot(direction, normalize(float3(-.68, .22, .58))));
                sky += float3(.13, .092, .042) * pow(sunward, 12) * (1 - altitude);
                return float4(sky, 1);
            }
            ENDHLSL
        }
    }
}
