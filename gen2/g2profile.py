"""Pinned X2D II 1.3.18.1 static-logo profile; no vendor image is bundled."""
HEADER=b'width:1024 height:768 offset-x:0 offset-y:0 format:2;'
SIZE=len(HEADER)+1024*768*3//2
STOCK='03fe95b65e39db203061b8f9d367a6f184d8c71bd9f5c7dd67b27ce0b1105576'
GUI='ea2cf8a5f6209f301971f55dd1ef86882c09db04b2e60371d2a243b79f1d8098'
ORIGINAL_USB='rndis,mass_storage,bulk,acm'


class StudioError(Exception):pass


def require(ok,message):
    if not ok:raise StudioError(message)
