namespace ShipRight.Modules.RemoteHost;

/// <summary>
/// Shell fragments that make a deploy safe to run over an earlier install, and keep the disk from filling up.
/// Data volumes are never removed or pruned: leftover containers are replaced, and volumes created by an
/// earlier compose project are copied into the fixed-name volumes the compose file now uses.
/// Each fragment runs inside the compose folder, as part of the single deploy command.
/// </summary>
public static class RemoteDeployPrep
{
    private static readonly string[] ContainerNames = ["shipright-db", "shipright-app", "shipright-caddy"];

    // new fixed volume name : key the earlier compose project used (its volume was <project>_<key>)
    private static readonly (string Name, string LegacyKey)[] Volumes =
    [
        ("shipright-db-data", "shipright-db-data"),
        ("shipright-app-data", "shipright-app-data"),
        ("shipright-caddy-data", "caddy-data"),
        ("shipright-caddy-config", "caddy-config"),
    ];

    public static string BuildCleanSlateFragment()
    {
        var names = string.Join(" ", ContainerNames);
        var vols = string.Join(" ", Volumes.Select(v => $"{v.Name}:{v.LegacyKey}"));
        return
            // 1. Replace containers left by another compose project (their fixed names would conflict). Volumes stay.
            "proj=$(docker compose config 2>/dev/null | sed -n 's/^name: //p' | head -n1); proj=${proj:-shipright}; " +
            $"for c in {names}; do " +
            "if docker inspect \"$c\" >/dev/null 2>&1; then " +
            "owner=$(docker inspect -f '{{index .Config.Labels \"com.docker.compose.project\"}}' \"$c\"); " +
            "if [ \"$owner\" != \"$proj\" ]; then " +
            "echo \"[deploy] Replacing leftover container $c from an earlier install (${owner:-unknown}). Its data volumes are kept.\"; " +
            "docker stop -t 60 \"$c\" >/dev/null 2>&1; docker rm \"$c\" >/dev/null || exit 1; " +
            "fi; fi; done; " +
            // 2. Adopt data that an earlier project stored in <project>_<key> volumes.
            $"for pair in {vols}; do " +
            "v=${pair%%:*}; key=${pair##*:}; " +
            "docker volume inspect \"$v\" >/dev/null 2>&1 && continue; " +
            "best=''; bestk=-1; " +
            "for l in $(docker volume ls -q | grep -E \"_${key}$\"); do " +
            "k=$(docker run --rm -v \"$l\":/d:ro alpine:3 du -sk /d 2>/dev/null | cut -f1); k=${k:-0}; " +
            "if [ \"$k\" -gt \"$bestk\" ]; then best=$l; bestk=$k; fi; done; " +
            "if [ -n \"$best\" ]; then " +
            "echo \"[deploy] Adopting data from volume $best ($bestk KB) into $v. The old volume is kept.\"; " +
            "docker volume create \"$v\" >/dev/null && " +
            "docker run --rm -v \"$best\":/from:ro -v \"$v\":/to alpine:3 sh -c 'cp -a /from/. /to/' || " +
            "{ echo \"[deploy] Copying data into $v failed; stopping so nothing is lost.\"; docker volume rm \"$v\" >/dev/null 2>&1; exit 1; }; " +
            "fi; done";
    }

    /// <summary>Prunes unused images when space is low; refuses to continue when it is critically low.</summary>
    public static string BuildDiskGuardFragment() =>
        "avail=$(df -Pm / | awk 'NR==2{print $4}'); echo \"[deploy] Free disk space: ${avail} MB\"; " +
        "if [ \"$avail\" -lt 3000 ]; then echo '[deploy] Disk space is low: removing unused Docker images first.'; " +
        "docker image prune -af 2>&1 | tail -n 1; avail=$(df -Pm / | awk 'NR==2{print $4}'); " +
        "if [ \"$avail\" -lt 1000 ]; then echo \"[deploy] Not enough disk space (${avail} MB free). Free some space or enlarge the disk, then deploy again.\"; exit 1; fi; fi";

    /// <summary>After a good start: drop dangling and week-old unused images and build cache. Never touches volumes. Cannot fail the deploy.</summary>
    public static string BuildPruneFragment() =>
        "echo '[deploy] Cleaning up unused Docker images…'; " +
        "docker image prune -f >/dev/null 2>&1; " +
        "docker image prune -af --filter 'until=168h' 2>&1 | tail -n 1; " +
        "docker builder prune -f --filter 'until=168h' >/dev/null 2>&1; " +
        "df -h / | tail -n 1; true";
}
