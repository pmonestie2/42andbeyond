"""Check missing station links without overwriting imported rooms or NPCs."""
from copy import deepcopy
import unittest
from complete_station_layouts import complete_layouts


class StationCompletionTests(unittest.TestCase):
    def test_missing_station_gets_services_without_copied_npcs(self):
        """Preserve imported content and isolate each standard service-room copy."""
        imported = [dict(id=10761, parent=1076, exterior=1, name='Dauntless', kind=0,
                         rooms=[dict(index=0, style=16)], terminals=[dict(room=0, kind=0)],
                         npcs=[dict(id=7)])]
        original = deepcopy(imported)
        sectors = [dict(id=4015, navpoints=[dict(id=100649, name='Somerled Station',
                        object_type=12, gate_destination=40151)])]
        result = complete_layouts(imported, sectors)
        self.assertEqual(imported, original)
        self.assertEqual(result[0], original[0])
        station = result[1]
        self.assertEqual((station['id'], station['parent'], station['exterior']), (40151, 4015, 100649))
        self.assertEqual(station['rooms'], imported[0]['rooms'])
        self.assertEqual(station['terminals'], imported[0]['terminals'])
        self.assertEqual(station['npcs'], [])
        self.assertEqual(complete_layouts(result, sectors), result)
        station['rooms'][0]['style'] = 99
        self.assertEqual(imported, original)

    def test_conflicting_exteriors_are_rejected(self):
        """Reject ambiguous identities instead of silently docking into the wrong station."""
        imported = [dict(id=10761, kind=0, rooms=[], terminals=[])]
        sectors = [dict(id=1, navpoints=[dict(id=1, object_type=12, name='A', gate_destination=123),
                                       dict(id=2, object_type=12, name='B', gate_destination=123)])]
        with self.assertRaises(ValueError):
            complete_layouts(imported, sectors)
