"""Install the built mod (the Package folder) into the game and make sure the game loads it.

    python deploy.py                  copy Package\\ into the mod folder and enable the mod in manifest.toml
    python deploy.py --dry-run        show what would change, change nothing
    python deploy.py --clean          also delete files in the mod folder that are not in the package
    python deploy.py --mod-dir "D:\\...\\mods\\newaa"

Run python build.py first. The mod folder is Documents\\My Games\\Kitten Space Agency\\mods\\newaa unless setup.py saved another.
Close the game (and StarMap) before deploying: they keep the DLLs locked.
"""
from __future__ import annotations

import argparse
import re
import shutil
import sys
from pathlib import Path
from typing import List, Optional, Tuple

import newaa_common as common
from newaa_common import die, fail, heading, note, ok, say, warn


def plan_copy(package: Path, mod_dir: Path) -> Tuple[List[Path], List[Path], List[Path]]:
    """Split the package's files into (new, changed, unchanged) relative to what is already in the mod folder."""
    new: List[Path] = []
    changed: List[Path] = []
    same: List[Path] = []
    for relative in common.files_under(package):
        target = mod_dir / relative
        if not target.is_file():
            new.append(relative)
        elif common.same_file(package / relative, target):
            same.append(relative)
        else:
            changed.append(relative)
    return new, changed, same


def copy_files(package: Path, mod_dir: Path, relatives: List[Path]) -> None:
    for relative in relatives:
        target = mod_dir / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(package / relative, target)


def stale_files(package: Path, mod_dir: Path) -> List[Path]:
    wanted = set(common.files_under(package))
    return [relative for relative in common.files_under(mod_dir) if relative not in wanted]


def line_ending(text: str) -> str:
    return "\r\n" if "\r\n" in text else "\n"


def manifest_state(text: str) -> str:
    """'missing', 'disabled' or 'enabled' for the mod's entry in the game's manifest.toml."""
    for block in re.split(r"(?m)^(?=\[\[mods\]\])", text):
        if re.search(rf'(?m)^\s*id\s*=\s*"{re.escape(common.MOD_ID)}"\s*$', block):
            if re.search(r"(?m)^\s*enabled\s*=\s*false\s*$", block):
                return "disabled"
            return "enabled"
    return "missing"


def with_mod_enabled(text: str) -> str:
    """The manifest text with the mod listed and enabled. Other entries are left exactly as they are."""
    newline = line_ending(text)
    if manifest_state(text) == "missing":
        base = text if (not text or text.endswith(("\n", "\r"))) else text + newline
        spacer = "" if not base.strip() else newline
        return f'{base}{spacer}[[mods]]{newline}id = "{common.MOD_ID}"{newline}enabled = true{newline}'
    blocks = re.split(r"(?m)^(?=\[\[mods\]\])", text)
    for index, block in enumerate(blocks):
        if re.search(rf'(?m)^\s*id\s*=\s*"{re.escape(common.MOD_ID)}"\s*$', block):
            blocks[index] = re.sub(r"(?m)^(\s*enabled\s*=\s*)false(\s*)$", r"\1true\2", block)
    return "".join(blocks)


def update_manifest(mod_dir: Path, dry_run: bool, assume_yes: bool, skip: bool) -> None:
    heading("Game manifest")
    manifest = common.game_data_dir(mod_dir) / "manifest.toml"
    if skip:
        note("Skipped (--no-manifest).")
        return
    if not manifest.is_file():
        warn(f"{manifest} does not exist yet. The game creates it the first time it runs. Run the game once through StarMap,"
             " then run this again so the mod is listed.")
        return
    with manifest.open("r", encoding="utf-8", newline="") as handle:  # newline="" keeps CRLF as it is
        text = handle.read()
    state = manifest_state(text)
    if state == "enabled":
        ok(f'"{common.MOD_ID}" is listed and enabled in {manifest.name}')
        return
    problem = "is not listed in" if state == "missing" else "is switched off in"
    warn(f'"{common.MOD_ID}" {problem} {manifest}. The game ignores the mod until it is listed and enabled.')
    if dry_run:
        note("Dry run: the manifest would be updated.")
        return
    if not common.ask_yes_no("Update the manifest now? (a backup is kept next to it)", True, assume_yes):
        warn("Left as is. Add this to the manifest yourself:")
        say(f'         [[mods]]\n         id = "{common.MOD_ID}"\n         enabled = true')
        return
    backup = manifest.with_name(manifest.name + ".newaa-backup")
    if not backup.exists():
        shutil.copy2(manifest, backup)
    with manifest.open("w", encoding="utf-8", newline="") as handle:
        handle.write(with_mod_enabled(text))
    ok(f"Updated {manifest.name} (backup: {backup.name})")


def main() -> int:
    parser = argparse.ArgumentParser(description="Install the built New AA mod into the game.")
    parser.add_argument("--mod-dir", help="the mod folder (default: the one setup.py saved, or Documents\\My Games\\Kitten Space Agency\\mods\\newaa)")
    parser.add_argument("--dry-run", action="store_true", help="show what would change without changing anything")
    parser.add_argument("--clean", action="store_true", help="delete files in the mod folder that are not in the package")
    parser.add_argument("--no-manifest", action="store_true", help="do not touch the game's manifest.toml")
    parser.add_argument("--yes", "-y", action="store_true", help="answer yes to every question")
    parser.add_argument("--force", action="store_true", help="deploy even if the game or StarMap is running (locked files will fail)")
    args = parser.parse_args()

    config = common.load_config()
    package = common.PACKAGE_DIR
    mod_dir = common.resolve_mod_dir(args.mod_dir, config)

    heading("Checks")
    if not package.is_dir():
        die("There is no Package folder. Run  python build.py  first.")
    problems = common.check_package(package)
    if problems:
        for problem in problems:
            fail(problem)
        die("The Package folder is incomplete. Run  python build.py  again.")
    ok(f"Package: {package}  ({len(common.files_under(package))} files)")
    note(f"Mod folder: {mod_dir}")
    if mod_dir.name.lower() != common.MOD_ID:
        warn(f'The mod folder is named "{mod_dir.name}". StarMap needs it to be "{common.MOD_ID}", matching the id in mod.toml.')

    running = common.running_game_processes()
    if running and not args.dry_run:
        message = "Running: " + ", ".join(running) + ". Close the game and StarMap first, because they lock the mod's DLLs."
        if args.force:
            warn(message + " (--force: trying anyway)")
        else:
            die(message + "  Use --force to try anyway.")
    elif running:
        warn("Running: " + ", ".join(running) + " (fine for a dry run, but close them before deploying)")
    else:
        ok("The game and StarMap are not running")

    heading("Copy")
    new, changed, same = plan_copy(package, mod_dir)
    say(f"  new {len(new)}, changed {len(changed)}, already up to date {len(same)}")
    for relative in new:
        say(f"    + {relative}")
    for relative in changed:
        say(f"    ~ {relative}")

    stale: List[Path] = []
    if args.clean and mod_dir.is_dir():
        stale = stale_files(package, mod_dir)
        for relative in stale:
            say(f"    - {relative}  (not in the package)")

    if args.dry_run:
        note("Dry run: nothing was changed.")
    else:
        try:
            copy_files(package, mod_dir, new + changed)
        except PermissionError as error:
            die(f"Could not write {error.filename}. Is the game or StarMap still running?")
        for relative in stale:
            try:
                (mod_dir / relative).unlink()
            except OSError as error:
                warn(f"could not delete {relative}: {error}")
        # Confirm what landed in the mod folder is exactly what is in the package.
        wrong = [r for r in common.files_under(package) if not common.same_file(package / r, mod_dir / r)]
        if wrong:
            for relative in wrong:
                fail(f"{relative} in the mod folder does not match the package")
            die("Deploy did not complete.")
        if new or changed or stale:
            ok(f"Copied {len(new) + len(changed)} file(s)" + (f", removed {len(stale)} stale file(s)" if stale else "")
               + f"; the mod folder matches the package")
        else:
            ok("The mod folder was already up to date")

    update_manifest(mod_dir, args.dry_run, args.yes, args.no_manifest)

    say()
    if args.dry_run:
        ok("Dry run finished.")
    else:
        ok("Deployed. Start the game through StarMap.exe, then pick DLAA, SMAA or FXAA under Settings > Display > Anti-Aliasing.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
