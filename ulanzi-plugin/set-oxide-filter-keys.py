"""Binds F13 and F14 to SDR Oxide's filter width, for the dial's held scan button.

Oxide reads its shortcuts only when it starts and writes them back when it closes, so close
it first. Neither key is on an ordinary keyboard. Running this twice changes nothing.
"""
import json
import os
import shutil
import subprocess
import sys
import time

INPUT = os.path.expandvars(r"%APPDATA%\sdroxide\sdroxide\config\input.json")
STEP_HZ = 1000.0


def binding(key, value):
    return {
        "chord": {"key": key, "ctrl": False, "shift": False, "alt": False},
        "action": "FilterWidth",
        "value": value,
        "tuning": {"step": STEP_HZ, "accel": 0.0, "invert": False},
        "button": "Momentary",
        "enabled": True,
    }


def main():
    running = subprocess.run(
        ["tasklist", "/fi", "imagename eq sdroxide.exe", "/nh"], capture_output=True, text=True
    ).stdout
    if "sdroxide.exe" in running:
        sys.exit("Najpierw zamknij SDR Oxide.")

    with open(INPUT, encoding="utf-8") as f:
        doc = json.load(f)
    doc["keys"] = [k for k in doc["keys"] if k["chord"]["key"] not in ("F13", "F14")]
    doc["keys"] += [binding("F13", 1.0), binding("F14", -1.0)]

    shutil.copyfile(INPUT, f"{INPUT}.bak-{time.strftime('%Y%m%d-%H%M%S')}")
    with open(INPUT, "w", encoding="utf-8", newline="") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)
    print("zapisano F13/F14 = FilterWidth")


if __name__ == "__main__":
    main()
