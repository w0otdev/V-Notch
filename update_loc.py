import re

file_path = r'Services\LocalizationService.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

replacements = [
    # English
    ('["intro.title"] = "INTRODUCING",', '["intro.title"] = "INTRODUCING",'),
    ('["intro.headline"] = "Liquid Glass Theme",', '["intro.headline"] = "Spotlight Search",'),
    ('["intro.body"] = "A new skin that turns the notch into liquid glass - refracting the desktop behind it in real time, with soft chromatic edges, depth, and a frosted blur.",', '["intro.body"] = "Lightning-fast file and app search powered by Everything IPC and OLE DB. Press Alt + Space to find anything instantly.",'),
    ('["intro.body"] = "A new skin that turns the notch into liquid glass — refracting the desktop behind it in real time, with soft chromatic edges, depth, and a frosted blur.",', '["intro.body"] = "Lightning-fast file and app search powered by Everything IPC and OLE DB. Press Alt + Space to find anything instantly.",'),
    
    # Vietnamese
    ('["intro.title"] = "GIỚI THIỆU",', '["intro.title"] = "GIỚI THIỆU",'),
    ('["intro.headline"] = "Theme Liquid Glass",', '["intro.headline"] = "Spotlight Search",'),
    ('["intro.body"] = "Giao diện mới biến notch thành kính lỏng - khúc xạ nền desktop phía sau theo thời gian thực, với viền tán sắc nhẹ, chiều sâu và lớp kính mờ.",', '["intro.body"] = "Tìm kiếm siêu tốc file và ứng dụng thông qua Everything IPC và OLE DB. Nhấn Alt + Space để tìm mọi thứ ngay lập tức.",'),
    ('["intro.body"] = "Giao diện mới biến notch thành kính lỏng — khúc xạ nền desktop phía sau theo thời gian thực, với viền tán sắc nhẹ, chiều sâu và lớp kính mờ.",', '["intro.body"] = "Tìm kiếm siêu tốc file và ứng dụng thông qua Everything IPC và OLE DB. Nhấn Alt + Space để tìm mọi thứ ngay lập tức.",'),
    
    # Spanish
    ('["intro.headline"] = "Tema Liquid Glass",', '["intro.headline"] = "Búsqueda Spotlight",'),
    ('["intro.body"] = "Un nuevo tema que convierte el notch en cristal líquido: refracta el escritorio detrás en tiempo real, con bordes cromáticos suaves, profundidad y desenfoque esmerilado.",', '["intro.body"] = "Búsqueda ultrarrápida de archivos y aplicaciones impulsada por Everything IPC y OLE DB. Presiona Alt + Espacio para encontrar cualquier cosa al instante.",'),

    # French
    ('["intro.headline"] = "Thème Liquid Glass",', '["intro.headline"] = "Recherche Spotlight",'),
    ('["intro.body"] = "Un nouveau thème qui transforme le notch en verre liquide - réfractant le bureau en temps réel avec des contours chromatiques doux, de la profondeur et un flou dépoli.",', '["intro.body"] = "Recherche ultra-rapide de fichiers et d\'applications via Everything IPC et OLE DB. Appuyez sur Alt + Espace pour tout trouver instantanément.",'),

    # German
    ('["intro.headline"] = "Liquid Glass Thema",', '["intro.headline"] = "Spotlight-Suche",'),
    ('["intro.body"] = "Ein neues Design, das die Notch in flüssiges Glas verwandelt - bricht den Desktop dahinter in Echtzeit mit weichen Farbrändern, Tiefe und Frost-Effekt.",', '["intro.body"] = "Blitzschnelle Datei- und App-Suche basierend auf Everything IPC und OLE DB. Drücken Sie Alt + Leertaste, um alles sofort zu finden.",'),

    # Japanese
    ('["intro.headline"] = "Liquid Glassテーマ",', '["intro.headline"] = "Spotlight 検索",'),
    ('["intro.body"] = "ノッチを液体のガラスに変える新しいスキン - 背後のデスクトップをリアルタイムで屈折させ、柔らかな色収差のエッジ、奥行き、すりガラスのぼかしを実現します。",', '["intro.body"] = "Everything IPC と OLE DB を搭載した超高速のファイル・アプリ検索。Alt + Space キーを押すだけで何でもすぐに見つかります。",'),

    # Hindi
    ('["intro.headline"] = "Liquid Glass थीम",', '["intro.headline"] = "स्पॉटलाइट खोज",'),
    ('["intro.body"] = "एक नया स्किन, जो नॉच को लिक्विड ग्लास में बदल देता है - डेस्कटॉप को रियल टाइम में रिफ्रैक्ट करता है, जिसमें सॉफ्ट क्रोमेटिक किनारे, गहराई और फ्रॉस्टेड ब्लर होता है।",', '["intro.body"] = "Everything IPC और OLE DB द्वारा संचालित बेहद तेज़ फ़ाइल और ऐप खोज। तुरंत कुछ भी खोजने के लिए Alt + Space दबाएं।",'),
]

for old, new in replacements:
    content = content.replace(old, new)

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)

print("Replaced!")
