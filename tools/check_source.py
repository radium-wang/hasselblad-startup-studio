"""检查待公开源码的隐私、文件类型与本地文档链接；不访问设备或网络。"""
import argparse
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ROOT_FILES = {'README.md', 'LICENSE', '.gitignore', 'AGENTS.md', 'CONTRIBUTING.md',
              'SECURITY.md', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md', 'BUILD.md', 'requirements-dev.txt'}
SOURCE_DIRS = {'native', 'tools', 'CodeTests', 'research', '.github', 'windows'}
SUFFIXES = {'.swift', '.c', '.h', '.S', '.py', '.md', '.txt', '.json', '.yml', '.yaml', '.cs', '.csproj', '.manifest', '.props', '.ps1'}
IGNORED = {'outputs', '__pycache__', '.venv', '.git', 'inputs', '.DS_Store'}
PRIVATE_PATTERNS = [
    r'/Users/[A-Za-z0-9_.-]+/', r'/home/[A-Za-z0-9_.-]+/',
    r'github_pat_[A-Za-z0-9_]{20,}', r'gh[pousr]_[A-Za-z0-9]{20,}',
    r'AKIA[0-9A-Z]{16}', r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
]


def source_files(root):
    files = []
    for path in sorted(root.rglob('*')):
        relative = path.relative_to(root)
        if any(part in IGNORED for part in relative.parts):
            continue
        if relative.parts[0] == 'windows' and any(part in {'bin', 'obj'} for part in relative.parts[1:]):
            continue
        if path.is_symlink():
            raise ValueError(f'Symlink is not a source input: {relative}')
        if not path.is_file():
            continue
        if relative.as_posix() == 'release-source.json':
            continue
        if len(relative.parts) == 1:
            if path.name not in ROOT_FILES:
                raise ValueError(f'Unlisted root input: {relative}')
        elif relative.parts[0] not in SOURCE_DIRS or path.suffix not in SUFFIXES:
            raise ValueError(f'Unlisted source input: {relative}')
        files.append(path)
    return files


def audit(root, verify_release=False):
    root = root.resolve()
    files = source_files(root)
    missing = ROOT_FILES - {p.name for p in files if p.parent == root}
    if missing:
        raise ValueError('Missing project files: ' + ', '.join(sorted(missing)))
    hashes = {}
    for path in files:
        relative = path.relative_to(root).as_posix()
        data = path.read_bytes()
        text = data.decode('utf-8')
        if '\0' in text:
            raise ValueError(f'Binary content in source: {relative}')
        for pattern in PRIVATE_PATTERNS:
            if re.search(pattern, text):
                raise ValueError(f'Private path or credential pattern: {relative}')
        if path.suffix == '.md':
            for target in re.findall(r'\[[^\]]*\]\(([^)]+)\)', text):
                if '://' in target or target.startswith('#'):
                    continue
                target = target.split('#')[0].strip('<>')
                dest = (path.parent / target).resolve()
                if not dest.is_relative_to(root) or not dest.exists():
                    raise ValueError(f'Missing or external document link in {relative}: {target}')
        hashes[relative] = hashlib.sha256(data).hexdigest()
    manifest = root / 'release-source.json'
    if verify_release and (not manifest.exists() or json.loads(manifest.read_text())['files'] != hashes):
        raise ValueError('Source hashes differ from release-source.json')
    return dict(passed=True, deviceAccessed=False, fileCount=len(files), files=hashes)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--report', type=Path)
    parser.add_argument('--verify-release', action='store_true', help='额外核对不可变源码包的 release-source.json')
    args = parser.parse_args()
    result = audit(args.root, verify_release=args.verify_release)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2) + '\n')
    print(f"PASS: {result['fileCount']} source files; links and privacy scan; no device access")


if __name__ == '__main__':
    main()
