#ifndef STREETY_ATMOSPHERE_INCLUDED
#define STREETY_ATMOSPHERE_INCLUDED

// shared by the tile and sky shaders so distant city and horizon fade into the same haze.
// every value here is a global set each frame by the mod (src/Streety/Render/Atmosphere.cs); while it isn't running
// they're all zero, which turns the haze off and leaves the sun straight up.

// x: haze extinction at sea level (1/m), y: 1 / haze scale height (1/m), z: sea level (world y), w: max ray length (m)
float4 _StreetyHaze;
// rgb: skylight scattered towards the eye by the haze, a: forward-scatter anisotropy (henyey-greenstein g)
float4 _StreetyHazeAmbient;
// xyz: direction towards the sun (even below the horizon), w: unused
float4 _StreetySunDirection;
// rgb: sunlight reaching the haze, already reddened and faded by the mod; a: how much of it glows around the sun
float4 _StreetySunColour;

float3 StreetySunDirection()
{
    float3 sun = _StreetySunDirection.xyz;
    return dot(sun, sun) > 0.25 ? sun : float3(0, 1, 0);
}

// optical depth of exponential height haze along a ray: integral of sigma0 * exp(-b * (h - sea)) over the ray
float StreetyOpticalDepth(float3 origin, float3 direction, float distance)
{
    float sigma = _StreetyHaze.x * exp(-_StreetyHaze.y * (origin.y - _StreetyHaze.z));
    float k = max(_StreetyHaze.y * direction.y * distance, -60.0);

    // (1 - e^-k) / k, which tends to 1 for rays running level
    float shape = abs(k) < 1e-3 ? 1.0 - 0.5 * k : (1.0 - exp(-k)) / k;
    return sigma * distance * shape;
}

float StreetyTransmittance(float3 origin, float3 direction, float distance)
{
    return exp(-StreetyOpticalDepth(origin, direction, min(distance, _StreetyHaze.w)));
}

// colour of fully hazed air looking along direction: skylight plus the sun's forward-scattered glow
float3 StreetyHazeColour(float3 direction)
{
    float g = _StreetyHazeAmbient.a;
    float cosTheta = dot(direction, StreetySunDirection());

    // henyey-greenstein normalised so isotropic scattering is 1
    float phase = (1.0 - g * g) / pow(max(1.0 + g * g - 2.0 * g * cosTheta, 1e-4), 1.5);

    return _StreetyHazeAmbient.rgb + _StreetySunColour.rgb * lerp(1.0, phase, _StreetySunColour.a);
}

float3 StreetyApplyHaze(float3 colour, float3 origin, float3 direction, float distance)
{
    float transmittance = StreetyTransmittance(origin, direction, distance);
    return colour * transmittance + StreetyHazeColour(direction) * (1.0 - transmittance);
}

float3 StreetyApplyHaze(float3 colour, float3 positionWS)
{
    float3 ray = positionWS - _WorldSpaceCameraPos;
    float distance = length(ray);
    return StreetyApplyHaze(colour, _WorldSpaceCameraPos, ray / max(distance, 1e-4), distance);
}

#endif
