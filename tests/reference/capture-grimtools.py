"""Capture a small public GrimTools data reference; never execute downloaded JS.

Run from the repository root: python tests/reference/capture-grimtools.py
The fixture is reviewed/committed separately. Complete remote bundles stay in
ignored artifacts/, not in the source tree or application distribution.
"""
import hashlib
import json
import argparse
from pathlib import Path
import re
from urllib.request import Request, urlopen
from urllib.error import HTTPError

ROOT = Path(__file__).resolve().parents[2]
REF = ROOT / "artifacts/grimtools-audit/reference"
REF.mkdir(parents=True, exist_ok=True)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--offline", action="store_true", help="Use previously downloaded public bundles in artifacts/")
options = parser.parse_args()


def fetch(url, name):
    if options.offline:
        data = (REF / name).read_bytes()
    else:
        request = Request(url, headers={"User-Agent": "Mozilla/5.0 (compatible; GrimDawnOptimizerReferenceAudit/1.0)"})
        try:
            with urlopen(request, timeout=45) as response:
                data = response.read()
        except HTTPError as error:
            raise SystemExit(f"Public reference returned HTTP {error.code}: {url}. "
                "If the bundles were downloaded separately into artifacts/grimtools-audit/reference, use --offline.") from error
        (REF / name).write_bytes(data)
    return data.decode("utf-8"), {"url": url, "sha256": hashlib.sha256(data).hexdigest().upper()}


html, page = fetch("https://www.grimtools.com/calc/", "calc.html")
version = re.search(r'class=mod-version>([^<]+)', html).group(1)
assets = {}
for name, path in [("calculator", "/calc/calc.js"), ("database", "/db/itemdb/itemdb.js")]:
    asset = re.search(re.escape(path) + r'\?\d+', html).group(0)
    assets[name] = fetch("https://www.grimtools.com" + asset, path.rsplit("/", 1)[1])


def scalar_object(source, name):
    literal = re.search(r'window\.' + name + r'=(\{.*?\});', source, re.S).group(1)
    # These particular objects contain JSON-compatible values; only their keys
    # are unquoted. Reject all other JavaScript syntax through json.loads.
    literal = re.sub(r'([,{]\s*)([A-Za-z_]\w*)\s*:', r'\1"\2":', literal)
    return json.loads(literal)


db = assets["database"][0]
formulas = scalar_object(db, "combatformulas")
engine = scalar_object(db, "engine")
bio = scalar_object(db, "playerBio")
skill = re.search(r'\bsk1133:\{(.*?)\},sk\d+:', assets["calculator"][0], re.S).group(1)
absorption = json.loads(re.search(r'defensiveAbsorptionModifier:(\[[\d,\s]+\])', skill).group(1))
maximum = int(re.search(r'\bskillMaxLevel:(\d+)', skill).group(1))
ultimate = int(re.search(r'\bskillUltimateLevel:(\d+)', skill).group(1))
fixture = {
    "source": {"version": version, "page": page, **{name: data[1] for name, data in assets.items()}},
    "armorChances": {key: value for key, value in formulas.items() if key.startswith("combatRegion")},
    "armorAbsorption": engine["armorDefensiveAbsorption"],
    "formulas": {key: formulas[key] for key in ["offensiveAbilityEquation", "defensiveAbilityEquation",
        "physicalDamageEquation", "pierceDamageEquation", "physicalDurationDamageEquation",
        "magicalDamageEquation", "magicalDurationDamageEquation", "meleeBlockEquation",
        "physicalDamageDefenseEquationDGP", "physcialDamageDefenseEquationDLEP"]},
    "attributeIncrements": {key: bio[key] for key in ["strengthIncrement", "cunningIncrement", "spiritIncrement",
        "healthIncrement", "energyIncrement", "healthIncrementCunning", "healthIncrementSpirit"]},
    "scarsOfBattle": {"sourceId": "sk1133", "skillMaxLevel": maximum, "skillUltimateLevel": ultimate,
        "defensiveAbsorptionModifier": absorption}
}
target = ROOT / "tests/GrimDawnOptimizer.Tests/Fixtures/grimtools-reference.json"
target.write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(f"Captured GrimTools {version}; reference: {target.relative_to(ROOT)}")
