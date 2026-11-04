"""Mutation tests for standalone, standard-library-only Foley package auditor."""
import hashlib
import importlib.util
import json
import struct
import tempfile
import unittest
import warnings
import zipfile
from pathlib import Path
import xml.etree.ElementTree as ET

script=Path(__file__).resolve().parents[2]/"scripts/audit_foley_dawproject.py"
spec=importlib.util.spec_from_file_location("foley_daw_audit",script)
audit=importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)

def wave(marker):
    audio=bytearray(16000)
    audio[123]=marker
    chunks=(b"fmt "+struct.pack("<IHHIIHH",16,1,1,8000,16000,2,16)+
            b"data"+struct.pack("<I",len(audio))+audio)
    return b"RIFF"+struct.pack("<I",len(chunks)+4)+b"WAVE"+chunks

class Fixture:
    def __init__(self,dir):
        self.root=Path(dir)
        self.zip=self.root/"export.dawproject"
        self.pre=self.root/"pre.json"
        self.timing=self.root/"timing.json"
        self.sounds=[wave(0),wave(42)]
        self.hashes=[hashlib.sha256(x).hexdigest() for x in self.sounds]
        self.pre_obj={
            "schema":"steinberg-foley-pre-daw-hashlocked-v1",
            "status":"human_attested_samples_pending_daw_timing_review",
            "host_action":"none","automatic_placement":False,"sound_event_timing":"NOT_VERIFIED",
            "approved_audio_count":2,
            "entries":[
                {"source_event_id":eid,"proposed_action_unconfirmed":action,
                 "sample_sha256":h,"video_sha256":"a"*64,
                 "audio_integrity_sha256_verified":True,"daw_placement_approved":False,
                 "action_timing_verified":False,"host_action":"none"}
                for eid,action,h in [("open","opening_door",self.hashes[0]),
                                      ("close","closing_door",self.hashes[1])]]
        }
        self.write_pre()
        self.timing_obj={
            "schema":"steinberg-foley-human-timing-review-v1",
            "host_action":"none","pre_daw_manifest_sha256":self.sha(self.pre),
            "video":{"fps_numerator":30000,"fps_denominator":1001,
                     "total_frames":9000,"timecode_mode":"drop_frame"},
            "assignments":[
                {"source_event_id":eid,"track_name":track,"sample_sha256":h,
                 "reviewed_window_sha256":"a"*64,"event_frame":frame,
                 "duration_frames":12,"human_confirmed_video_frame":True,
                 "human_confirmed_track":True,
                 "human_confirmed_source_window_to_video_mapping":True,
                 "reviewer_note":"SYNTHETIC TEST ONLY"}
                for eid,track,h,frame in [
                    ("open","FOLEY_DOOR_OPEN",self.hashes[0],1800),
                    ("close","FOLEY_DOOR_CLOSE",self.hashes[1],3600)]]
        }
        self.write_timing()
        self.project=ET.Element("Project",version="1.0")
        structure=ET.SubElement(self.project,"Structure")
        root=ET.SubElement(ET.SubElement(self.project,"Arrangement"),"Lanes",timeUnit="seconds")
        for i,(name,time,h) in enumerate([
            ("FOLEY_DOOR_OPEN","60.06",self.hashes[0]),
            ("FOLEY_DOOR_CLOSE","120.12",self.hashes[1])]):
            tid="track"+str(i)
            ET.SubElement(structure,"Track",name=name,id=tid)
            lane=ET.SubElement(root,"Lanes",track=tid)
            clips=ET.SubElement(lane,"Clips")
            clip=ET.SubElement(clips,"Clip",name=name,time=time,duration="1",
                               playStart="0",contentTimeUnit="seconds")
            audio=ET.SubElement(clip,"Audio",duration="1",channels="1",sampleRate="8000")
            ET.SubElement(audio,"File",path="audio/"+h+".wav")
        self.audio=dict(zip(["audio/"+h+".wav" for h in self.hashes],self.sounds))
        self.save()
    def sha(self,file):
        return hashlib.sha256(file.read_bytes()).hexdigest()
    def write_pre(self):
        self.pre.write_text(json.dumps(self.pre_obj),encoding="utf8")
    def write_timing(self):
        self.timing.write_text(json.dumps(self.timing_obj),encoding="utf8")
    def save(self,duplicate=False,extra=None):
        with zipfile.ZipFile(self.zip,"w") as z:
            z.writestr("project.xml",ET.tostring(self.project))
            z.writestr("metadata.xml",b"<MetaData/>")
            for path,payload in self.audio.items():
                z.writestr(path,payload)
            if duplicate:
                with warnings.catch_warnings():
                    warnings.simplefilter("ignore",UserWarning)
                    z.writestr("project.xml",ET.tostring(self.project))
            if extra:
                z.writestr(extra,b"bad")
    def verify(self):
        return audit.audit(self.zip,self.pre,self.timing)

class TestFoleyPortableAudit(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.f=Fixture(self.temp.name)
    def tearDown(self):
        self.temp.cleanup()
    def bad(self,code):
        with self.assertRaisesRegex(audit.AuditError,code):
            self.f.verify()
    def test_valid_two_track_2997_dropframe(self):
        r=self.f.verify()
        self.assertEqual(r["status"],"PASS_INDEPENDENT_FOLEY_DAWPROJECT_AUDIT_ONLY")
        self.assertEqual((r["clips"],r["tracks"],r["media"]),(2,2,2))
    def test_edited_wav_bytes(self):
        self.f.audio["audio/"+self.f.hashes[1]+".wav"]=wave(77)
        self.f.save()
        self.bad("EMBEDDED_WAV_SHA_MISMATCH")
    def test_one_frame_timing_shift(self):
        self.f.project.findall(".//Clip")[0].set("time","60.093366667")
        self.f.save()
        self.bad("WRONG_FRAME_TO_CLIP_MAPPING")
    def test_duplicate_track(self):
        self.f.project.findall(".//Track")[1].set("name","FOLEY_DOOR_OPEN")
        self.f.save()
        self.bad("DUPLICATE_FOLEY_TRACK")
    def test_unreferenced_media(self):
        self.f.audio["audio/"+hashlib.sha256(wave(11)).hexdigest()+".wav"]=wave(11)
        self.f.save()
        self.bad("MISSING_OR_ORPHAN_WAV")
    def test_missing_media(self):
        del self.f.audio["audio/"+self.f.hashes[1]+".wav"]
        self.f.save()
        self.bad("MISSING_OR_UNSAFE_AUDIO_REF")
    def test_path_traversal(self):
        self.f.save(extra="../evil.exe")
        self.bad("UNSAFE_ZIP_ENTRY")
    def test_duplicate_zip_member(self):
        self.f.save(duplicate=True)
        self.bad("DUPLICATE_ZIP_ENTRIES")
    def test_approval_hash_changed(self):
        self.f.timing_obj["pre_daw_manifest_sha256"]="f"*64
        self.f.write_timing()
        self.bad("TIMING_MANIFEST_SHA_MISMATCH")
    def test_human_flag_false(self):
        self.f.timing_obj["assignments"][0]["human_confirmed_track"]=False
        self.f.write_timing()
        self.bad("TIMING_NOT_REVIEWED")
    def test_nan_clip(self):
        self.f.project.findall(".//Clip")[0].set("time","NaN")
        self.f.save()
        self.bad("NONFINITE_NUMBER")
    def test_wrong_wav_duration(self):
        self.f.project.findall(".//Clip")[0].set("duration","0.5")
        self.f.save()
        self.bad("WAV_DURATION_OR_HEADER_MISMATCH")
    def test_xml_dtd(self):
        with zipfile.ZipFile(self.f.zip,"w") as z:
            z.writestr("project.xml",b'<!DOCTYPE a [<!ENTITY x SYSTEM "file:///etc/passwd">]><Project/>')
            z.writestr("metadata.xml",b"<MetaData/>")
            for name,payload in self.f.audio.items(): z.writestr(name,payload)
        self.bad("UNSAFE_XML")

if __name__=="__main__":
    unittest.main()
