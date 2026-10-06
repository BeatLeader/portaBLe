using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace PortableHub;

/// <summary>portaBLe's database bucket: list the .db keys and download one.</summary>
public sealed class S3Service(HubConfig config)
{
    private readonly AmazonS3Client _client = new(config.S3.AccessKey, config.S3.SecretKey, RegionEndpoint.GetBySystemName(config.S3.Region));
    private (DateTimeOffset At, List<S3Db> Items)? _cache;

    public async Task<List<S3Db>> List(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cache is { } c && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(2)) return c.Items;
        var items = new List<S3Db>();
        var request = new ListObjectsV2Request { BucketName = config.S3.Bucket };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, ct);
            items.AddRange((response.S3Objects ?? new()).Where(o => o.Key.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                .Select(o => new S3Db(o.Key, o.Size, new DateTimeOffset(o.LastModified.ToUniversalTime(), TimeSpan.Zero))));
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
        items.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        _cache = (DateTimeOffset.UtcNow, items);
        return items;
    }

    public async Task Download(string key, string destination, TextWriter log, CancellationToken ct)
    {
        log.WriteLine($"downloading s3://{config.S3.Bucket}/{key}");
        var tmp = destination + ".download";
        using (var response = await _client.GetObjectAsync(config.S3.Bucket, key, ct))
        await using (var input = response.ResponseStream)
        await using (var output = File.Create(tmp))
        {
            var buffer = new byte[1 << 20];
            long total = 0, nextReport = 100L << 20;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                if (total >= nextReport)
                {
                    log.WriteLine($"  {total >> 20} / {response.ContentLength >> 20} MB");
                    log.Flush();
                    nextReport += 100L << 20;
                }
            }
        }
        File.Move(tmp, destination, overwrite: true);
        log.WriteLine($"downloaded {key} ({new FileInfo(destination).Length >> 20} MB)");
    }
}
