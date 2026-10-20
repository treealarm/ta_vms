using System.Buffers.Binary;

namespace VmsAnalyticsSingleton;

internal static class EmbeddingCodec
{
  /// <summary>
  /// The worker's embedding_model as stored: null when it names none (a worker that predates the
  /// field), which is the same space as the rows written before the column existed.
  /// </summary>
  public static string? ModelOrNull(string? model) => string.IsNullOrEmpty(model) ? null : model;

  public static bool TryEncodeNormalized(IReadOnlyList<float> source, out byte[] bytes, out int dim)
  {
    bytes = [];
    dim = 0;
    if (source.Count == 0)
      return false;

    var normSq = 0.0;
    for (var i = 0; i < source.Count; i++)
    {
      var v = source[i];
      if (!float.IsFinite(v))
        return false;
      normSq += v * v;
    }

    if (normSq <= 0.0)
      return false;

    var norm = Math.Sqrt(normSq);
    var buffer = new byte[source.Count * sizeof(float)];
    for (var i = 0; i < source.Count; i++)
      BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(i * sizeof(float), sizeof(float)), (float)(source[i] / norm));

    bytes = buffer;
    dim = source.Count;
    return true;
  }

  public static float[] DecodeNormalized(byte[] bytes, int dim)
  {
    if (dim <= 0 || bytes.Length != dim * sizeof(float))
      return [];

    var values = new float[dim];
    var normSq = 0.0;
    for (var i = 0; i < dim; i++)
    {
      var v = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
      values[i] = v;
      normSq += v * v;
    }

    if (normSq <= 0.0)
      return [];

    var norm = (float)Math.Sqrt(normSq);
    for (var i = 0; i < dim; i++)
      values[i] /= norm;

    return values;
  }

  public static float Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
  {
    if (left.Count == 0 || left.Count != right.Count)
      return 0f;

    var sum = 0f;
    for (var i = 0; i < left.Count; i++)
      sum += left[i] * right[i];
    return sum;
  }

  /// <summary>
  /// Best cosine of <paramref name="candidate"/> against any compatible probe vector.
  /// Face tracks especially need this: one pose can sit at 0.26 to another of the same person,
  /// so a single nearest-in-time sample is a fragile probe.
  /// </summary>
  public static float MaxCosine(IReadOnlyList<float[]> probes, IReadOnlyList<float> candidate)
  {
    var best = 0f;
    var any = false;
    foreach (var probe in probes)
    {
      if (probe.Length == 0 || probe.Length != candidate.Count)
        continue;
      var score = Cosine(probe, candidate);
      if (!any || score > best)
      {
        best = score;
        any = true;
      }
    }
    return any ? best : 0f;
  }
}
