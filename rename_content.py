import os

root_dir = r"d:\Project\DesktopKomik"
target_exts = {".cs", ".axaml", ".csproj", ".sln", ".iss", ".json", ".xaml"}

print(f"Scanning {root_dir}")

for subdir, dirs, files in os.walk(root_dir):
    # Skip .git folders
    if ".git" in dirs:
        dirs.remove(".git")
        
    for file in files:
        ext = os.path.splitext(file)[1]
        if ext in target_exts:
            filepath = os.path.join(subdir, file)
            try:
                with open(filepath, 'r', encoding='utf-8') as f:
                    content = f.read()
                
                if "MyMangaApp" in content:
                    print(f"Modifying {file}...")
                    new_content = content.replace("MyMangaApp", "Yomic")
                    
                    with open(filepath, 'w', encoding='utf-8') as f:
                        f.write(new_content)
            except Exception as e:
                print(f"Error processing {filepath}: {e}")
                
print("Done.")
