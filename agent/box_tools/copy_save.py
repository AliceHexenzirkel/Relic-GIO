#!/usr/bin/env python3
"""Copy a GIO account's progress (source uid) over another uid, on the same stack.

Runs ON THE BOX (needs docker + the stack up). Stdlib-only. The agent's /server/account/copy
does the same thing from the launcher; this is the hand tool for an SSH session.

  python3 copy_save.py /home/1.6_live <src_uid> <dst_uid>          # dry-run (writes nothing)
  python3 copy_save.py /home/1.6_live <src_uid> <dst_uid> --apply  # writes

What it does (see docs/derisk/ACCOUNT-COPY-HANDOFF.md for the background):
  - refuses if either uid is online (redis PlayerStatus:{uid}) or if the source saved recently;
  - t_player_data: copies the source row into the destination's shard with bin_data TRANSFORMED:
    every varint/fixed64 value with high32 == src becomes (dst<<32)|low32 -- persistent GUIDs
    are (uid<<32)|seq and would otherwise stay duplicated across accounts (co-op collision);
    the fields 7.2 (cur_scene_owner_uid), 28.5 (recent_mp_player_uid_list) and
    28.14 (friend_remark_name_map) are dropped if present;
  - t_block_data / t_home_data: copied server-side (the blobs carry no uid/guid);
  - does NOT touch t_player_uid (the destination must already have a uid), redis or sdk.db;
  - a mysqldump backup of the destination rows before deleting them, in /root/relic_backups/.

Self-test before any apply: the inverse transformation (dst->src) applied to the result must
reproduce the original bytes EXACTLY; otherwise the script refuses to write.
"""
import base64
import os
import subprocess
import sys
import time
import zlib

DROP_PATHS = {(7, 2), (28, 5), (28, 14)}
# The paths where GUIDs (uid<<32)|seq are EXPECTED (from the PlayerDataBin descriptors extracted
# from the gameserver). Rewrites on other paths are not blocked, but reported loudly at dry-run.
EXPECTED_GUID_PATHS = {
    (5, 1, 1, 3),        # item_bin.pack_store.item_list[].guid (fixed64)
    (2, 1, 3),           # avatar_bin.avatar_list[].guid (varint)
    (2, 1, 13, 3),       # avatar_list[].equip_list[].guid
    (2, 2), (2, 15),     # cur_avatar_guid, choose_avatar_guid
    (2, 5, 2, 1),        # team_map[].value.avatar_guid_list (packed)
    (2, 5, 2, 3),        # team_map[].value -- guid varint (leader/current)
    (2, 17),             # temp_avatar_guid_list
    (2, 1, 101, 2),      # avatar_list[].101.equip_guid_list (packed)
}
# Packed GUID lists: a message "guessed" from their bytes would hide the first GUID in a tag
# (an impossible field number such as 536870923) -- here we do NOT try a message parse.
KNOWN_PACKED_GUID_PATHS = {(2, 1, 101, 2), (2, 5, 2, 1), (2, 17)}


class ParseError(Exception):
    pass


def read_varint(buf, i):
    result = 0
    shift = 0
    while True:
        if i >= len(buf) or shift > 63:
            raise ParseError("truncated/too long varint at offset %d" % i)
        b = buf[i]
        i += 1
        result |= (b & 0x7F) << shift
        if not b & 0x80:
            return result, i
        shift += 7


def encode_varint(v):
    out = bytearray()
    while True:
        b = v & 0x7F
        v >>= 7
        if v:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def try_packed_varints(data):
    vals = []
    i = 0
    try:
        while i < len(data):
            v, i = read_varint(data, i)
            vals.append(v)
    except ParseError:
        return None
    return vals


def transform(buf, src, dst, path=(), log=None):
    """Returns (new_bytes, changed). Unchanged stretches are copied byte-for-byte, so a message
    with no rewrite comes out identical -- that is what guarantees the roundtrip test."""
    out = bytearray()
    i = 0
    changed = False
    while i < len(buf):
        start = i
        tag, i = read_varint(buf, i)
        fn, wt = tag >> 3, tag & 7
        # 2^29-1 = the protobuf maximum. Any guid "read" as a tag gives fn >= 2^29, so the limit
        # makes it impossible to swallow the first guid of a packed list into a tag.
        if fn == 0 or fn > 536870911:
            raise ParseError("invalid field number %d" % fn)
        p = path + (fn,)
        tag_end = i
        if wt == 0:
            v, i = read_varint(buf, i)
            if p in DROP_PATHS:
                changed = True
                log.append(("drop", p, v))
                continue
            if v >> 32 == src:
                changed = True
                log.append(("varint", p, v))
                out += buf[start:tag_end]
                out += encode_varint((dst << 32) | (v & 0xFFFFFFFF))
                continue
            out += buf[start:i]
        elif wt == 1:
            if i + 8 > len(buf):
                raise ParseError("truncated fixed64")
            v = int.from_bytes(buf[i:i + 8], "little")
            raw_end = i + 8
            i = raw_end
            if p in DROP_PATHS:
                changed = True
                log.append(("drop", p, v))
                continue
            if v >> 32 == src:
                changed = True
                log.append(("fixed64", p, v))
                out += buf[start:tag_end]
                out += ((dst << 32) | (v & 0xFFFFFFFF)).to_bytes(8, "little")
                continue
            out += buf[start:raw_end]
        elif wt == 2:
            ln, data_start = read_varint(buf, i)
            data_end = data_start + ln
            if data_end > len(buf):
                raise ParseError("truncated length-delimited field")
            data = buf[data_start:data_end]
            i = data_end
            if p in DROP_PATHS:
                changed = True
                log.append(("drop", p, "<%d bytes>" % ln))
                continue
            new = None
            if p in KNOWN_PACKED_GUID_PATHS:
                packed = try_packed_varints(data)
                if packed is None:
                    log.append(("PACKED-UNPARSABLE", p, "<%d bytes>" % ln))
                elif any(v >> 32 == src for v in packed):
                    log.append(("packed", p, sum(1 for v in packed if v >> 32 == src)))
                    new = b"".join(
                        encode_varint((dst << 32) | (v & 0xFFFFFFFF)) if v >> 32 == src
                        else encode_varint(v) for v in packed)
                if packed is not None:
                    if new is None:
                        out += buf[start:data_end]
                    else:
                        changed = True
                        out += buf[start:tag_end]
                        out += encode_varint(len(new))
                        out += new
                    continue
            sublog = []
            try:
                sub, subchanged = transform(data, src, dst, p, sublog)
                if subchanged:
                    new = sub
                    log.extend(sublog)
            except ParseError:
                packed = try_packed_varints(data)
                if packed is not None and any(v >> 32 == src for v in packed):
                    log.append(("PACKED-WARNING", p, [v for v in packed if v >> 32 == src]))
                    new = b"".join(
                        encode_varint((dst << 32) | (v & 0xFFFFFFFF)) if v >> 32 == src
                        else encode_varint(v) for v in packed)
            if new is None:
                out += buf[start:data_end]
            else:
                changed = True
                out += buf[start:tag_end]
                out += encode_varint(len(new))
                out += new
        elif wt == 5:
            if i + 4 > len(buf):
                raise ParseError("truncated fixed32")
            out += buf[start:i + 4]
            i += 4
        else:
            raise ParseError("wiretype %d (group?) at offset %d" % (wt, start))
    return bytes(out), changed


def scan_high32(buf, path=()):
    """Count varint/fixed64 values by high32, recursively (best effort -- subtrees that do not
    parse as messages are ignored). For verification, not for transformation."""
    counts = {}
    i = 0
    while i < len(buf):
        tag, i = read_varint(buf, i)
        fn, wt = tag >> 3, tag & 7
        if fn == 0 or fn > 536870911:
            raise ParseError("invalid field %d" % fn)
        if wt == 0:
            v, i = read_varint(buf, i)
            counts[v >> 32] = counts.get(v >> 32, 0) + 1
        elif wt == 1:
            v = int.from_bytes(buf[i:i + 8], "little")
            i += 8
            counts[v >> 32] = counts.get(v >> 32, 0) + 1
        elif wt == 2:
            ln, ds = read_varint(buf, i)
            i = ds + ln
            if i > len(buf):
                raise ParseError("truncated")
            try:
                for k, n in scan_high32(buf[ds:i]).items():
                    counts[k] = counts.get(k, 0) + n
            except ParseError:
                pass
        elif wt == 5:
            i += 4
        else:
            raise ParseError("wiretype %d" % wt)
    return counts


def raw_residual(buf, uid):
    """Count occurrences of uid in high32 straight on the bytes, INDEPENDENT of the parser (catches
    GUIDs the parser would miss). (a) a varint at any offset; (b) a LE u64 window. May have rare
    false positives -- compared before/after, never taken as absolute truth."""
    varint_hits = []
    fixed_hits = []
    for i in range(len(buf)):
        if buf[i] & 0x80:
            try:
                v, end = read_varint(buf, i)
                if v >> 32 == uid and end - i >= 5:
                    varint_hits.append(i)
            except ParseError:
                pass
        if i + 8 <= len(buf):
            if int.from_bytes(buf[i:i + 8], "little") >> 32 == uid:
                fixed_hits.append(i)
    return varint_hits, fixed_hits


# -- stack access --
def run(args, stdin_bytes=None):
    r = subprocess.run(args, input=stdin_bytes, stdout=subprocess.PIPE,
                       stderr=subprocess.PIPE)
    if r.returncode != 0:
        raise SystemExit("command failed (%d): %s\n%s" % (
            r.returncode, " ".join(args[:8]), r.stderr.decode("utf-8", "replace")[:2000]))
    return r.stdout


def compose(stack, *args, stdin_bytes=None):
    return run(["docker", "compose", "--project-directory", stack, "exec", "-T"] + list(args),
               stdin_bytes=stdin_bytes)


def read_env(stack, key):
    with open(os.path.join(stack, ".env")) as f:
        for line in f:
            if line.startswith(key + "="):
                return line.split("=", 1)[1].strip()
    raise SystemExit("%s not found in %s/.env" % (key, stack))


def mysql(stack, sql, raw=False):
    pw = read_env(stack, "MYSQL_ROOT_PASSWORD")
    args = ["-e", "MYSQL_PWD=" + pw, "mysql", "mysql", "-uroot", "-N",
            "--default-character-set=utf8mb4", "hk4e_db_user"]
    if raw:
        args.insert(-1, "--raw")
    return compose(stack, *args, stdin_bytes=sql.encode()).decode("utf-8", "replace")


def redis_pw(stack):
    with open(os.path.join(stack, "docker-compose.yml")) as f:
        for line in f:
            if "requirepass" in line:
                return line.split("requirepass", 1)[1].split()[0]
    raise SystemExit("requirepass not found in docker-compose.yml")


def uid_online(stack, uid):
    out = compose(stack, "redis", "redis-cli", "-a", redis_pw(stack), "--no-auth-warning",
                  "-n", "7", "EXISTS", "PlayerStatus:{%d}" % uid)
    return out.strip() == b"1"


def fetch_blob(stack, table, uid, col="bin_data", extra_where=""):
    out = mysql(stack, "SELECT TO_BASE64(%s) FROM %s WHERE uid=%d%s;" % (col, table, uid, extra_where))
    txt = out.replace("\\n", "").replace("\n", "").strip()
    if not txt or txt == "NULL":
        return None
    return base64.b64decode(txt)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    apply_mode = "--apply" in sys.argv
    if len(args) != 3:
        raise SystemExit(__doc__)
    stack = args[0].rstrip("/")
    src, dst = int(args[1]), int(args[2])
    if src == dst:
        raise SystemExit("source and destination are the same uid")
    s_shard, d_shard = src % 10, dst % 10

    # -- guards --
    rows = mysql(stack, "SELECT uid FROM t_player_uid WHERE uid IN (%d,%d);" % (src, dst)).split()
    have = {int(x) for x in rows}
    if src not in have:
        raise SystemExit("source uid %d does not exist in t_player_uid" % src)
    if dst not in have:
        raise SystemExit("destination uid %d does not exist in t_player_uid -- allocate it first (INSERT)" % dst)
    for u in (src, dst):
        if uid_online(stack, u):
            raise SystemExit("uid %d seems ONLINE (PlayerStatus in redis) -- refusing" % u)
    age = mysql(stack, "SELECT TIMESTAMPDIFF(SECOND, last_save_time, NOW()) FROM t_player_data_%d WHERE uid=%d;"
                % (s_shard, src)).strip()
    if not age:
        raise SystemExit("source uid %d has no row in t_player_data_%d" % (src, s_shard))
    if int(age) < 180:
        raise SystemExit("the source saved %ss ago -- seems to be in game; refusing" % age)

    # -- source blob + transformation --
    blob = fetch_blob(stack, "t_player_data_%d" % s_shard, src)
    wrapped = blob[:4] == b"ZLIB"
    payload = zlib.decompress(blob[4:]) if wrapped else blob
    print("source blob: %d B (%s), decompressed %d B" % (
        len(blob), "ZLIB" if wrapped else "raw", len(payload)))

    log = []
    new_payload, changed = transform(payload, src, dst, (), log)

    # self-test: the inverse transformation must give the EXACT original (if nothing was dropped)
    drops = [e for e in log if e[0] == "drop"]
    if not drops:
        back, _ = transform(new_payload, dst, src, (), [])
        if back != payload:
            raise SystemExit("SELF-TEST FAILED: the dst->src roundtrip does not reproduce the original -- writing NOTHING")
        print("roundtrip self-test: OK (dst->src reproduces the original bytes exactly)")
    else:
        print("roundtrip self-test: skipped (%d fields dropped)" % len(drops))

    by_kind = {}
    unexpected = set()
    for kind, p, v in log:
        by_kind[kind] = by_kind.get(kind, 0) + 1
        if kind in ("varint", "fixed64", "PACKED-WARNING") and p not in EXPECTED_GUID_PATHS:
            unexpected.add(p)
    print("rewrites: %s" % (by_kind or "none"))
    paths = sorted({p for _, p, _ in log})
    print("paths touched: %s" % ", ".join(".".join(map(str, p)) for p in paths))
    if unexpected:
        print("WARNING -- rewrites on UNEXPECTED paths (check by hand): %s"
              % ", ".join(".".join(map(str, p)) for p in sorted(unexpected)))

    before = scan_high32(payload)
    after = scan_high32(new_payload)
    print("structured scan, values with high32==%d: before %d, after %d | high32==%d after: %d"
          % (src, before.get(src, 0), after.get(src, 0), dst, after.get(dst, 0)))
    bv, bf = raw_residual(payload, src)
    av, af = raw_residual(new_payload, src)
    dv, df = raw_residual(new_payload, dst)
    print("raw byte scan (varint/fixed64) with high32==%d: before %d/%d, after %d/%d; "
          "high32==%d after: %d/%d" % (src, len(bv), len(bf), len(av), len(af), dst, len(dv), len(df)))
    if after.get(src, 0):
        raise SystemExit("%d values with high32==%d remain after the transformation -- NOT writing" % (after[src], src))
    if av or af:
        for off in (av + af)[:10]:
            print("  raw residue at offset %d: %s" % (off, new_payload[max(0, off - 8):off + 16].hex()))
        accept = 0
        for a in sys.argv:
            if a.startswith("--accept-raw-residuals="):
                accept = int(a.split("=", 1)[1])
        if len(av) + len(af) > accept:
            raise SystemExit(
                "the raw scan still sees high32==%d after the transformation (%d varint, %d fixed64). "
                "Inspect the context above: if they are false positives (e.g. float bytes), "
                "run with --accept-raw-residuals=%d" % (src, len(av), len(af), len(av) + len(af)))
        print("residues accepted explicitly (--accept-raw-residuals=%d)" % accept)

    new_blob = (b"ZLIB" + zlib.compress(new_payload, 6)) if wrapped else new_payload
    n_blocks = int(mysql(stack, "SELECT COUNT(*) FROM t_block_data_%d WHERE uid=%d;" % (s_shard, src)))
    n_home = int(mysql(stack, "SELECT COUNT(*) FROM t_home_data_%d WHERE uid=%d;" % (s_shard, src)))
    print("to copy: 1 player row (%d B new blob), %d block rows, %d home rows"
          % (len(new_blob), n_blocks, n_home))

    if not apply_mode:
        print("\nDRY-RUN -- nothing written. Run with --apply to apply.")
        return

    # -- destination backup --
    ts = time.strftime("%Y%m%d_%H%M%S")
    os.makedirs("/root/relic_backups", exist_ok=True)
    pw = read_env(stack, "MYSQL_ROOT_PASSWORD")
    dump = run(["docker", "compose", "--project-directory", stack, "exec", "-T",
                "-e", "MYSQL_PWD=" + pw, "mysql", "mysqldump", "-uroot", "--no-create-info",
                "--where=uid=%d" % dst, "hk4e_db_user",
                "t_player_data_%d" % d_shard, "t_block_data_%d" % d_shard, "t_home_data_%d" % d_shard])
    bpath = "/root/relic_backups/acctcopy_uid%d_%s.sql" % (dst, ts)
    with open(bpath, "wb") as f:
        f.write(dump)
    print("destination backup: %s (%d B)" % (bpath, len(dump)))

    # -- write --
    hexblob = new_blob.hex()
    sql = []
    sql.append("DELETE FROM t_player_data_%d WHERE uid=%d;" % (d_shard, dst))
    sql.append(
        "INSERT INTO t_player_data_%d (uid,nickname,level,exp,vip_point,json_data,bin_data,"
        "extra_bin_data,data_version,tag_list,before_login_bin_data) "
        "SELECT %d,nickname,level,exp,vip_point,json_data,0x%s,extra_bin_data,data_version,"
        "tag_list,before_login_bin_data FROM t_player_data_%d WHERE uid=%d;"
        % (d_shard, dst, hexblob, s_shard, src))
    sql.append("DELETE FROM t_block_data_%d WHERE uid=%d;" % (d_shard, dst))
    sql.append(
        "INSERT INTO t_block_data_%d (uid,block_id,data_version,bin_data) "
        "SELECT %d,block_id,data_version,bin_data FROM t_block_data_%d WHERE uid=%d;"
        % (d_shard, dst, s_shard, src))
    if n_home:
        sql.append("DELETE FROM t_home_data_%d WHERE uid=%d;" % (d_shard, dst))
        sql.append(
            "INSERT INTO t_home_data_%d (uid,bin_data,data_version) "
            "SELECT %d,bin_data,data_version FROM t_home_data_%d WHERE uid=%d;"
            % (d_shard, dst, s_shard, src))
    mysql(stack, "START TRANSACTION;\n" + "\n".join(sql) + "\nCOMMIT;")
    print("written.")

    # -- post-write verification --
    check = fetch_blob(stack, "t_player_data_%d" % d_shard, dst)
    cp = zlib.decompress(check[4:]) if check[:4] == b"ZLIB" else check
    if cp != new_payload:
        raise SystemExit("VERIFICATION FAILED: the blob read back differs from the one written!")
    ccounts = scan_high32(cp)
    nb = int(mysql(stack, "SELECT COUNT(*) FROM t_block_data_%d WHERE uid=%d;" % (d_shard, dst)))
    print("verification: blob read back identical; high32==%d: %d, high32==%d: %d; %d/%d block rows"
          % (src, ccounts.get(src, 0), dst, ccounts.get(dst, 0), nb, n_blocks))
    print("DONE. uid %d now has the progress of uid %d." % (dst, src))


if __name__ == "__main__":
    main()
