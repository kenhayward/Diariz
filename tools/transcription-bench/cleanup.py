"""Find and remove ONLY the recordings this benchmark created.

api_floor.py deletes its own recordings on a clean exit. This exists for the case it did not:
an interrupted run, a network failure mid-cleanup, or --keep.

Matching is on Recording.Title, which the bench sets to "floor-bench <n>s". Never on Name: the
summariser overwrites Name with a generated title (and will happily invent a plausible-looking
meeting name for synthetic audio), so a name-based filter stops matching the moment
summarisation runs. Title is the auto descriptor and stays stable.

    DIARIZ_TOKEN=dz_api_... python cleanup.py --base http://host:8080            # dry run
    DIARIZ_TOKEN=dz_api_... python cleanup.py --base http://host:8080 --delete
"""
import argparse

import _client


def main() -> None:
    ap = argparse.ArgumentParser()
    _client.add_common_args(ap)
    ap.add_argument("--delete", action="store_true", help="actually delete (default is a dry run)")
    args = ap.parse_args()
    base, token = _client.resolve(args)

    everything = _client.list_recordings(base, token)
    mine = [r for r in everything if str(r.get("title", "")).startswith(_client.MARKER)]
    print(f"{len(everything)} recordings on the instance; "
          f"{len(mine)} match title prefix {_client.MARKER!r}\n")
    for r in mine:
        print(f"  {r['id']}  {str(r.get('createdAt'))[:19]}  {r.get('title')}  (name: {r.get('name')})")

    if not mine:
        print("nothing to do.")
        return
    if not args.delete:
        print(f"\ndry run - pass --delete to remove these {len(mine)}.")
        return

    print(f"\ndeleting {len(mine)}...")
    ok = 0
    for r in mine:
        status, body = _client.request("DELETE", f"{base}/api/recordings/{r['id']}", token)
        if status in (200, 204):
            ok += 1
        else:
            print(f"  FAILED {r['id']}: HTTP {status} {str(body)[:120]}")
    print(f"deleted {ok}/{len(mine)}")


if __name__ == "__main__":
    main()
