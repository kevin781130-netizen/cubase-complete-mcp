#!/usr/bin/env python3
"""Independent stdlib-only Foley DAWproject audit. Does not import or edit a DAW.
Official XSD validation remains in the separately executed .NET test gate.
"""
import argparse
import hashlib
import json
import math
import re
import struct
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

MAX_JSON=2*1024*1024
MAX_ARCHIVE=513*1024*1024
MAX_WAV=256*1024*1024
AUDIO=re.compile(r"audio/([0-9a-f]{64})\.wav\Z")
TRACKS={"opening_door":"FOLEY_DOOR_OPEN","closing_door":"FOLEY_DOOR_CLOSE"}
FPS={(24,1),(24000,1001),(25,1),(30,1),(30000,1001),(50,1),(60,1),(60000,1001)}

class AuditError(Exception):
    pass

def need(condition, reason):
    if not condition:
        raise AuditError(reason)

def file_bytes(path, limit):
    p=Path(path)
    need(p.is_file() and not p.is_symlink() and 0<p.stat().st_size<=limit,"INVALID_INPUT_FILE")
    return p.read_bytes()

def read_json(path):
    raw=file_bytes(path,MAX_JSON)
    obj=json.loads(raw)
    need(isinstance(obj,dict),"INVALID_JSON")
    return obj,hashlib.sha256(raw).hexdigest()

def xml(raw):
    need(0<len(raw)<=1024*1024,"INVALID_XML_LENGTH")
    need(b"<!DOCTYPE" not in raw.upper() and b"<!ENTITY" not in raw.upper(),"UNSAFE_XML")
    try:
        return ET.fromstring(raw)
    except ET.ParseError as exc:
        raise AuditError("MALFORMED_XML") from exc

def num(value):
    try:
        n=float(value)
    except (TypeError,ValueError,OverflowError) as exc:
        raise AuditError("INVALID_NUMERIC_VALUE") from exc
    need(math.isfinite(n),"NONFINITE_NUMBER")
    return n

def wav_meta(blob):
    need(44<=len(blob)<=MAX_WAV and blob[:4]==b"RIFF" and blob[8:12]==b"WAVE","INVALID_WAV")
    need(struct.unpack_from("<I",blob,4)[0]+8==len(blob),"RIFF_SIZE_MISMATCH")
    offset,fmt,length,items=12,None,None,0
    while offset<len(blob):
        need(offset+8<=len(blob) and items<128,"INVALID_RIFF_CHUNKS")
        tag,n=struct.unpack_from("<4sI",blob,offset)
        start=offset+8
        offset=start+n+(n&1)
        need(offset<=len(blob),"TRUNCATED_RIFF_CHUNK")
        if tag==b"fmt ":
            need(fmt is None and 16<=n<=4096,"INVALID_WAV_FMT")
            fmt=struct.unpack_from("<HHIIHH",blob,start)
        elif tag==b"data":
            need(length is None,"DUPLICATE_WAV_DATA")
            length=n
        items+=1
    need(fmt is not None and length is not None and length>0,"MISSING_WAV_DATA")
    codec,channels,rate,byte_rate,align,bits=fmt
    need(codec in (1,3) and 1<=channels<=2 and 8000<=rate<=192000
         and bits in (8,16,24,32) and (codec==1 or bits==32)
         and align==channels*(bits//8) and byte_rate==align*rate and length%align==0,
         "INCONSISTENT_WAV_HEADER")
    return (channels,rate,length/byte_rate)

def audit(archive,pre_path,timing_path):
    pre,pre_sha=read_json(pre_path)
    timing,_=read_json(timing_path)
    need(pre.get("schema")=="steinberg-foley-pre-daw-hashlocked-v1" and
         pre.get("status")=="human_attested_samples_pending_daw_timing_review" and
         pre.get("host_action")=="none" and pre.get("automatic_placement") is False and
         pre.get("sound_event_timing")=="NOT_VERIFIED","UNAPPROVED_PRE_DAW")
    need(timing.get("schema")=="steinberg-foley-human-timing-review-v1" and
         timing.get("host_action")=="none" and timing.get("pre_daw_manifest_sha256")==pre_sha,
         "TIMING_MANIFEST_SHA_MISMATCH")
    entries,assign=pre.get("entries"),timing.get("assignments")
    need(isinstance(entries,list) and 1<=len(entries)<=8 and
         len(entries)==pre.get("approved_audio_count") and
         isinstance(assign,list) and len(assign)==len(entries),"INVALID_EVENT_COUNT")
    by_id={}
    for e in entries:
        need(isinstance(e,dict),"INVALID_ENTRY")
        eid=e.get("source_event_id")
        h=e.get("sample_sha256")
        need(isinstance(eid,str) and 0<len(eid)<=100 and eid not in by_id and
             e.get("proposed_action_unconfirmed") in TRACKS and
             isinstance(h,str) and re.fullmatch(r"[0-9a-f]{64}",h) and
             e.get("audio_integrity_sha256_verified") is True and
             e.get("daw_placement_approved") is False and
             e.get("action_timing_verified") is False and e.get("host_action")=="none",
             "INVALID_APPROVED_ENTRY")
        by_id[eid]=e
    v=timing.get("video")
    need(isinstance(v,dict),"INVALID_VIDEO_REVIEW")
    fps=(v.get("fps_numerator"),v.get("fps_denominator"))
    need(fps in FPS,"INVALID_DECLARED_FPS")
    mode=v.get("timecode_mode")
    need(mode in ("drop_frame","non_drop_frame") and
         (mode!="drop_frame" or fps in {(30000,1001),(60000,1001)}),"INVALID_TIMECODE_MODE")
    frames=v.get("total_frames")
    need(type(frames) is int and 0<frames<=20_000_000,"INVALID_FRAME_COUNT")
    expected={}
    seen=set()
    for a in assign:
        need(isinstance(a,dict),"INVALID_FRAME_REVIEW")
        eid=a.get("source_event_id")
        need(isinstance(eid,str) and eid in by_id and eid not in seen,"DUPLICATE_EVENT_OR_UNKNOWN")
        seen.add(eid)
        e=by_id[eid]
        track=TRACKS[e["proposed_action_unconfirmed"]]
        need(a.get("track_name")==track and a.get("sample_sha256")==e["sample_sha256"] and
             a.get("reviewed_window_sha256")==e.get("video_sha256"),"TIMING_PROVENANCE_INVALID")
        start,duration=a.get("event_frame"),a.get("duration_frames")
        need(type(start) is int and type(duration) is int and
             0<=start<frames and 0<=duration<=frames-start and
             all(a.get(k) is True for k in (
                 "human_confirmed_video_frame","human_confirmed_track",
                 "human_confirmed_source_window_to_video_mapping")) and
             isinstance(a.get("reviewer_note"),str) and
             0<len(a["reviewer_note"].strip())<=300,"TIMING_NOT_REVIEWED")
        expected.setdefault(track,[]).append((round(start*fps[1]/fps[0],9),e["sample_sha256"]))
    need(len(seen)==len(by_id),"INCOMPLETE_ASSIGNMENTS")
    archive_file=Path(archive)
    need(archive_file.is_file() and not archive_file.is_symlink() and
         0<archive_file.stat().st_size<=MAX_ARCHIVE,"INVALID_DAWPROJECT_SIZE")
    try:
        with zipfile.ZipFile(archive_file) as z:
            members=z.infolist()
            names=[m.filename for m in members]
            need(len(names)==len(set(names)) and len(names)>=3,"DUPLICATE_ZIP_ENTRIES")
            need({"project.xml","metadata.xml"}.issubset(names) and
                 all(n in ("project.xml","metadata.xml") or AUDIO.fullmatch(n) for n in names),
                 "UNSAFE_ZIP_ENTRY")
            need(sum(i.file_size for i in members)<=MAX_ARCHIVE and
                 all(i.file_size<=MAX_WAV and not i.is_dir() for i in members
                     if AUDIO.fullmatch(i.filename)),"ZIP_BOMB_OR_DIR")
            need(z.testzip() is None,"ZIP_CRC_INVALID")
            project=xml(z.read("project.xml"))
            meta=xml(z.read("metadata.xml"))
            need(project.tag=="Project" and meta.tag=="MetaData","INVALID_XML_ROOTS")
            root=project.find("Arrangement/Lanes")
            need(root is not None and root.get("timeUnit")=="seconds","NONSECOND_TIMELINE")
            tracks={}
            for t in project.findall("Structure/Track"):
                name,tid=t.get("name"),t.get("id")
                if name in TRACKS.values():
                    need(tid and tid not in tracks and name not in tracks.values(),"DUPLICATE_FOLEY_TRACK")
                    tracks[tid]=name
                else:
                    need(name=="Master","UNEXPECTED_DAW_TRACK")
            need(set(tracks.values())==set(expected),"WRONG_TRACK_SET")
            found={k:[] for k in expected}
            for lane in root:
                need(lane.tag=="Lanes" and lane.get("track") in tracks,"UNEXPECTED_TRACK_LANE")
                name=tracks[lane.get("track")]
                for seq in lane:
                    need(seq.tag=="Clips","UNEXPECTED_LANE_CHILD")
                    for clip in seq:
                        need(clip.tag=="Clip" and clip.get("contentTimeUnit")=="seconds"
                             and num(clip.get("playStart"))==0,"INVALID_CLIP")
                        pos,duration=num(clip.get("time")),num(clip.get("duration"))
                        need(pos>=0 and duration>0,"INVALID_CLIP_TIME")
                        audio=clip.find("Audio")
                        need(audio is not None,"MISSING_AUDIO_TAG")
                        f=audio.find("File")
                        media=f.get("path") if f is not None else None
                        need(isinstance(media,str) and AUDIO.fullmatch(media) and media in names,
                             "MISSING_OR_UNSAFE_AUDIO_REF")
                        found[name].append((pos,AUDIO.fullmatch(media).group(1),duration,
                                            num(audio.get("duration")),
                                            int(audio.get("channels","-1")),
                                            int(audio.get("sampleRate","-1"))))
            source_hashes={h for rows in expected.values() for _,h in rows}
            media_hashes={AUDIO.fullmatch(n).group(1) for n in names if AUDIO.fullmatch(n)}
            need(source_hashes==media_hashes,"MISSING_OR_ORPHAN_WAV")
            actual={}
            for h in source_hashes:
                payload=z.read("audio/"+h+".wav")
                need(hashlib.sha256(payload).hexdigest()==h,"EMBEDDED_WAV_SHA_MISMATCH")
                actual[h]=wav_meta(payload)
            for track,rows in expected.items():
                got=found[track]
                need(len(got)==len(rows),"MISSING_OR_EXTRA_CLIP")
                for observed,claimed in zip(sorted(got),sorted(rows)):
                    pos,h,dur,audio_dur,channels,rate=observed
                    sec,wav_hash=claimed
                    need(h==wav_hash and abs(pos-sec)<=5e-9,"WRONG_FRAME_TO_CLIP_MAPPING")
                    expected_ch,expected_rate,wav_dur=actual[h]
                    need(channels==expected_ch and rate==expected_rate and
                         abs(dur-wav_dur)<=2e-6 and abs(audio_dur-wav_dur)<=2e-6,
                         "WAV_DURATION_OR_HEADER_MISMATCH")
    except (zipfile.BadZipFile,zipfile.LargeZipFile,KeyError,ValueError,TypeError,struct.error) as exc:
        raise AuditError("MALFORMED_ZIP_OR_MEDIA") from exc
    return {"status":"PASS_INDEPENDENT_FOLEY_DAWPROJECT_AUDIT_ONLY",
            "clips":sum(map(len,expected.values())),"tracks":len(expected),
            "media":len(source_hashes),"rights_and_real_timing":"NOT_VERIFIED",
            "cubase_nuendo_import":"NOT_RUN"}

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("archive")
    p.add_argument("pre_daw_manifest")
    p.add_argument("timing_review_json")
    args=p.parse_args()
    try:
        report=audit(args.archive,args.pre_daw_manifest,args.timing_review_json)
    except (AuditError,OSError,json.JSONDecodeError,UnicodeDecodeError,ET.ParseError) as exc:
        print(json.dumps({"status":"AUDIT_FAIL_CLOSED","reason":str(exc)}))
        return 1
    print(json.dumps(report,sort_keys=True))
    return 0

if __name__=="__main__":
    sys.exit(main())
