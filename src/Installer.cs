using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

public sealed class FileEntry {
    public string path, original_sha256, patched_sha256, patch, patch_sha256;
    public string[] accepted_previous_sha256;
    public long source_size, target_size;
}
public sealed class Guard { public string path, sha256; }
public sealed class Package { public int schema, translated_entries, language_slot; public string version, game; public FileEntry[] files; public Guard[] guards; }

public sealed class Engine {
    public const string ManifestHash = "__MANIFEST_SHA256__";
    public readonly string PackageRoot;
    public readonly Package Data;
    public Action<int,string> Progress = delegate {};
    static readonly string Separator = Path.DirectorySeparatorChar.ToString();
    static bool Same(string a,string b) { return String.Equals(a,b,StringComparison.OrdinalIgnoreCase); }
    public static string Hash(string path) { using (var stream=File.OpenRead(path)) using(var hash=SHA256.Create()) return Hex(hash.ComputeHash(stream)); }
    static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant(); }
    public static string Within(string root,string relative) {
        root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Некорректный путь в пакете.");
        string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root+Separator,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь выходит за пределы выбранной папки.");
        return full;
    }
    public Engine(string root) {
        PackageRoot=Path.GetFullPath(root);
        string manifest=Within(root,"package.json");
        if (!File.Exists(manifest) || !Same(Hash(manifest),ManifestHash)) throw new InvalidDataException("Пакет неполный или повреждён. Распакуйте архив целиком в новую папку.");
        Data=new JavaScriptSerializer().Deserialize<Package>(File.ReadAllText(manifest,Encoding.UTF8));
        if (Data==null || Data.schema!=1 || Data.files==null || Data.files.Length!=12 || Data.guards==null || Data.guards.Length!=2) throw new InvalidDataException("Неизвестный формат пакета.");
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var f in Data.files) {
            if (!Regex.IsMatch(f.path??"",@"\A(?:resources\.assets|level[0-9]+)\z") || !paths.Add(f.path) || f.source_size<0 || f.target_size<0 || f.source_size>1073741824L || f.target_size>1073741824L) throw new InvalidDataException("Некорректная запись файла в пакете.");
            if (!Regex.IsMatch(f.original_sha256??"",@"\A[0-9a-f]{64}\z") || !Regex.IsMatch(f.patched_sha256??"",@"\A[0-9a-f]{64}\z") || !Regex.IsMatch(f.patch_sha256??"",@"\A[0-9a-f]{64}\z")) throw new InvalidDataException("Некорректные контрольные суммы.");
            Within(root,f.patch);
        }
    }
    public static string CanonicalGame(string game) {
        if (String.IsNullOrWhiteSpace(game)) throw new IOException("Выберите папку игры.");
        game=Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        if (!File.Exists(Path.Combine(game,"TheDollShop.exe")) || !Directory.Exists(Path.Combine(game,"TheDollShop_Data"))) throw new IOException("Выберите папку с TheDollShop.exe, а не папку TheDollShop_Data.");
        return game;
    }
    static void CheckClosed(string game) {
        string exe=Path.Combine(game,"TheDollShop.exe");
        foreach(var p in Process.GetProcessesByName("TheDollShop")) {
            try { if (Same(Path.GetFullPath(p.MainModule.FileName),exe)) throw new IOException("Сначала закройте The Doll Shop, затем повторите установку."); }
            catch(System.ComponentModel.Win32Exception) { throw new IOException("Не удалось проверить процесс игры. Закройте The Doll Shop и повторите попытку."); }
            finally { p.Dispose(); }
        }
    }
    bool Allowed(FileEntry f,string hash) {
        if (Same(hash,f.original_sha256) || Same(hash,f.patched_sha256)) return true;
        if (f.accepted_previous_sha256!=null) foreach(string previous in f.accepted_previous_sha256) if(Same(hash,previous)) return true;
        return false;
    }
    public Dictionary<string,string> Validate(string game,bool restoring,bool checkClosed) {
        game=CanonicalGame(game); if(checkClosed)CheckClosed(game);
        string data=Within(game,"TheDollShop_Data"),backup=Within(game,"TheDollShopRussian_Backup");
        if (!restoring) foreach(var g in Data.guards) {
            if (g.path!="Managed\\AC.dll" && g.path!="Managed\\Assembly-CSharp.dll") throw new InvalidDataException("Некорректная проверка версии.");
            string guard=Within(data,g.path);
            if(!File.Exists(guard) || !Same(Hash(guard),g.sha256)) throw new IOException("Эта версия игры не поддерживается. Файлы игры не заменены.");
        }
        var hashes=new Dictionary<string,string>();
        foreach(var f in Data.files) {
            string target=Within(data,f.path),saved=Within(backup,f.path);
            if(!File.Exists(target)) throw new IOException("Не найден файл игры: "+f.path);
            string hash=Hash(target);hashes.Add(f.path,hash);
            if(!Allowed(f,hash)) throw new IOException("Файл игры изменён или относится к другой версии: "+f.path+". Восстановите оригинал через Steam или используйте подходящую версию пакета.");
            if(File.Exists(saved)) {
                if(!Same(Hash(saved),f.original_sha256)) throw new IOException("Резервная копия отличается от оригинала: "+f.path+". Автоматическая замена остановлена.");
            } else if(!Same(hash,f.original_sha256)) throw new IOException("Для установленного русификатора отсутствует оригинальная резервная копия: "+f.path);
            if(!restoring) {
                string delta=Within(PackageRoot,f.patch);
                if(!File.Exists(delta) || !Same(Hash(delta),f.patch_sha256)) throw new InvalidDataException("Повреждён бинарный патч: "+f.path+". Заново распакуйте архив.");
            }
        }
        return hashes;
    }
    static void Transfer(Stream source,Stream destination,long count,byte[] buffer) {
        while(count>0) {
            int n=source.Read(buffer,0,(int)Math.Min(count,buffer.Length));
            if(n<=0)throw new InvalidDataException("Бинарный патч оборван.");
            destination.Write(buffer,0,n);count-=n;
        }
    }
    public static void Apply(string original,string delta,string output,FileEntry f) {
        using(var raw=File.OpenRead(delta)) using(var zipped=new GZipStream(raw,CompressionMode.Decompress)) using(var reader=new BinaryReader(zipped,Encoding.UTF8))
        using(var source=File.OpenRead(original)) using(var target=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
            if(Encoding.ASCII.GetString(reader.ReadBytes(8))!="DSDELTA1") throw new InvalidDataException("Неизвестный бинарный патч.");
            long sourceLength=reader.ReadInt64(),targetLength=reader.ReadInt64();
            string oldHash=Hex(reader.ReadBytes(32)),newHash=Hex(reader.ReadBytes(32));
            if(sourceLength!=source.Length || sourceLength!=f.source_size || targetLength!=f.target_size || !Same(oldHash,f.original_sha256) || !Same(newHash,f.patched_sha256)) throw new InvalidDataException("Патч не соответствует исходному файлу.");
            int commands=reader.ReadInt32();if(commands<0 || commands>1000000)throw new InvalidDataException("Некорректное число команд.");
            byte[] buffer=new byte[65536];long written=0;
            for(int i=0;i<commands;i++) {
                byte kind=reader.ReadByte();long offset=0;int length;
                if(kind==0) { offset=reader.ReadInt64();length=reader.ReadInt32(); }
                else if(kind==1) length=reader.ReadInt32();
                else throw new InvalidDataException("Некорректная команда патча.");
                if(length<=0 || written>targetLength-length)throw new InvalidDataException("Размер команды выходит за пределы файла.");
                if(kind==0) {
                    if(offset<0 || offset>sourceLength-length)throw new InvalidDataException("Чтение выходит за пределы оригинального файла.");
                    source.Position=offset;Transfer(source,target,length,buffer);
                } else Transfer(reader.BaseStream,target,length,buffer);
                written+=length;
            }
            if(written!=targetLength || zipped.ReadByte()!=-1)throw new InvalidDataException("Размер восстановленного файла не совпадает.");
            target.Flush(true);
        }
        if(!Same(Hash(output),f.patched_sha256))throw new InvalidDataException("Контрольная сумма восстановленного файла не совпадает.");
    }
    sealed class Pending { public FileEntry Entry; public string Target,Stage,Rollback,BeforeHash; }
    public string Run(string selected,bool restoring) {
        string game=CanonicalGame(selected),data=Within(game,"TheDollShop_Data"),backup=Within(game,"TheDollShopRussian_Backup");
        Progress(2,"Проверяю файлы игры и резервные копии…");
        var hashes=Validate(game,restoring,true);bool done=true;
        foreach(var f in Data.files)if(!Same(hashes[f.path],restoring?f.original_sha256:f.patched_sha256))done=false;
        if(done)return restoring?"Оригинальные файлы уже восстановлены.":"Русификатор "+Data.version+" уже установлен.";
        long needed=16777216;foreach(var f in Data.files)needed+=f.target_size*2+f.source_size;
        try { var drive=new DriveInfo(Path.GetPathRoot(data));if(drive.AvailableFreeSpace<needed)throw new IOException("Недостаточно свободного места. Для установки требуется около "+(needed/1048576)+" МБ."); }
        catch(ArgumentException) { }
        string suffix=".dsru-"+Guid.NewGuid().ToString("N");var pending=new List<Pending>();var committed=new List<Pending>();bool succeeded=false;
        try {
            // Build and verify every result before replacing a live game file.
            int index=0;
            foreach(var f in Data.files) {
                index++;if(Same(hashes[f.path],restoring?f.original_sha256:f.patched_sha256))continue;
                string target=Within(data,f.path),saved=Within(backup,f.path);
                string stage=Within(data,f.path+suffix+".new"),rollback=Within(data,f.path+suffix+".old");
                var item=new Pending{Entry=f,Target=target,Stage=stage,Rollback=rollback,BeforeHash=hashes[f.path]};pending.Add(item);
                Progress(5+index*60/Data.files.Length,restoring?"Подготавливаю оригинальные файлы…":"Подготавливаю русский перевод…");
                if(restoring) {
                    File.Copy(saved,stage,false);if(!Same(Hash(stage),f.original_sha256))throw new IOException("Ошибка проверки оригинальной копии.");
                } else {
                    string original=File.Exists(saved)?saved:target;
                    Apply(original,Within(PackageRoot,f.patch),stage,f);
                }
            }
            CheckClosed(game);
            foreach(var item in pending)if(!Same(Hash(item.Target),item.BeforeHash))throw new IOException("Файл игры изменился во время подготовки. Повторите после закрытия игры.");
            if(!restoring) {
                Directory.CreateDirectory(backup);
                foreach(var f in Data.files) {
                    string saved=Within(backup,f.path);
                    if(!File.Exists(saved))File.Copy(Within(data,f.path),saved,false);
                    if(!Same(Hash(saved),f.original_sha256))throw new IOException("Резервная копия не прошла проверку: "+f.path);
                }
            }
            int n=0;
            foreach(var item in pending) {
                Progress(70+(++n)*25/pending.Count,restoring?"Восстанавливаю оригиналы…":"Устанавливаю перевод…");
                File.Replace(item.Stage,item.Target,item.Rollback,true);committed.Add(item);
                if(!Same(Hash(item.Target),restoring?item.Entry.original_sha256:item.Entry.patched_sha256))throw new IOException("Проверка установленного файла не пройдена.");
            }
            succeeded=true;Progress(100,"Готово.");
            return restoring?"Оригинальные файлы восстановлены. Резервная копия сохранена.":"Русификатор установлен. Запустите игру через Steam и выберите «Русский».";
        } catch {
            for(int i=committed.Count-1;i>=0;i--) {
                var item=committed[i];
                if(File.Exists(item.Rollback))File.Replace(item.Rollback,item.Target,null,true);
            }
            throw;
        } finally {
            foreach(var item in pending) {
                if(File.Exists(item.Stage))File.Delete(item.Stage);
                // A rollback snapshot is removed only when the destination has
                // the committed result or the exact previous state.
                if(File.Exists(item.Rollback)) {
                    string actual=Hash(item.Target);
                    if(succeeded || Same(actual,item.BeforeHash))File.Delete(item.Rollback);
                }
            }
        }
    }
    public static string FindGame() {
        var roots=new List<string>();
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))if(key!=null) { string root=key.GetValue("SteamPath") as string;if(!String.IsNullOrEmpty(root))roots.Add(root); }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam"));
        var libraries=new List<string>(roots);
        foreach(string root in roots) {
            string vdf=Path.Combine(root,@"steamapps\libraryfolders.vdf");if(!File.Exists(vdf))continue;
            foreach(Match match in Regex.Matches(File.ReadAllText(vdf),"\"path\"\\s*\"([^\"]+)\""))libraries.Add(match.Groups[1].Value.Replace(@"\\",@"\"));
        }
        foreach(string library in libraries) { string game=Path.Combine(library,@"steamapps\common\The Doll Shop");if(File.Exists(Path.Combine(game,"TheDollShop.exe")))return game; }
        return "";
    }
}

public sealed class InstallerForm : Form {
    readonly TextBox pathBox=new TextBox();
    readonly Button install=new Button(),restore=new Button(),browse=new Button();
    readonly Label state=new Label();readonly ProgressBar progress=new ProgressBar();
    readonly string packageRoot;bool working;
    public InstallerForm(string root,string chosen) {
        packageRoot=root;Text="The Doll Shop — Русский перевод";ClientSize=new Size(780,440);MinimumSize=Size;MaximumSize=Size;
        FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
        BackColor=Color.FromArgb(248,247,244);Font=new Font("Segoe UI",11);AutoScaleMode=AutoScaleMode.Font;
        FormClosing+=delegate(object sender,FormClosingEventArgs e) { if(working)e.Cancel=true; };
        var title=new Label{Text="The Doll Shop",Font=new Font("Segoe UI",23,FontStyle.Bold),Location=new Point(28,22),Size=new Size(710,44)};
        var subtitle=new Label{Text="Неофициальный русский перевод · версия 0.1.1",Location=new Point(30,74),Size=new Size(710,27),ForeColor=Color.FromArgb(90,88,85)};
        var explanation=new Label{Text="Установка перевода и уменьшенного шрифта меню.\nСохранения и прогресс остаются на месте.",Location=new Point(30,114),Size=new Size(710,52)};
        var folder=new Label{Text="Папка игры (с файлом TheDollShop.exe)",Location=new Point(30,181),Size=new Size(620,26)};
        pathBox.SetBounds(30,214,589,30);pathBox.Text=String.IsNullOrEmpty(chosen)?Engine.FindGame():chosen;
        browse.Text="Выбрать…";browse.SetBounds(632,210,118,36);browse.Click+=delegate { using(var dialog=new FolderBrowserDialog{Description="Выберите папку с TheDollShop.exe",SelectedPath=pathBox.Text})if(dialog.ShowDialog(this)==DialogResult.OK)pathBox.Text=dialog.SelectedPath; };
        install.Text="Установить";install.SetBounds(30,265,224,43);install.BackColor=Color.FromArgb(55,78,65);install.ForeColor=Color.White;install.FlatStyle=FlatStyle.Flat;install.Click+=delegate { Start(false); };
        restore.Text="Восстановить оригиналы";restore.SetBounds(268,265,273,43);restore.Click+=delegate { Start(true); };
        progress.SetBounds(30,325,720,17);state.SetBounds(30,354,720,62);
        state.Text=String.IsNullOrEmpty(pathBox.Text)?"Выберите папку игры, затем нажмите «Установить».":"Игра найдена. Закройте её перед установкой.";
        Controls.AddRange(new Control[]{title,subtitle,explanation,folder,pathBox,browse,install,restore,progress,state});
    }
    void Busy(bool busy) { working=busy;install.Enabled=restore.Enabled=browse.Enabled=pathBox.Enabled=!busy; }
    void Start(bool restoring) {
        string selected=pathBox.Text;Busy(true);progress.Value=0;state.Text="Проверяю пакет…";
        ThreadPool.QueueUserWorkItem(delegate {
            string result=null;Exception error=null;
            try {
                var engine=new Engine(packageRoot);
                engine.Progress=delegate(int value,string message) { BeginInvoke((Action)delegate { progress.Value=Math.Max(0,Math.Min(100,value));state.Text=message; }); };
                result=engine.Run(selected,restoring);
            } catch(Exception ex) { error=ex; }
            BeginInvoke((Action)delegate {
                Busy(false);
                if(error is UnauthorizedAccessException) {
                    state.Text="Для этой папки нужны права администратора.";
                    if(MessageBox.Show(this,"Перезапустить установщик с правами администратора?",Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes) {
                        try { Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--game "+Quote(selected)){UseShellExecute=true,Verb="runas"});Close(); }
                        catch(System.ComponentModel.Win32Exception) { state.Text="Запрос прав отменён. Можно повторить установку позже."; }
                    }
                } else if(error!=null) { state.Text="Установка остановлена.";MessageBox.Show(this,error.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error); }
                else { progress.Value=100;state.Text=result; }
            });
        });
    }
    public static string Quote(string path) { return "\""+path.Replace("\"","")+"\""; }
}
public static class Program {
    [STAThread]public static int Main(string[] args) {
        string root=AppDomain.CurrentDomain.BaseDirectory,game="",mode="",preview="";
        for(int i=0;i<args.Length;i++) {
            if(args[i]=="--game" && i+1<args.Length)game=args[++i];
            else if(args[i]=="--install" || args[i]=="--restore" || args[i]=="--check")mode=args[i];
            else if(args[i]=="--preview" && i+1<args.Length)preview=args[++i];
        }
        if(mode!="") {
            try {
                var engine=new Engine(root);engine.Progress=delegate(int p,string m){Console.WriteLine(p+"% "+m);};
                if(mode=="--check") { engine.Validate(game,false,true);Console.WriteLine("Проверка пройдена."); }
                else Console.WriteLine(engine.Run(game,mode=="--restore"));
                return 0;
            } catch(Exception ex) { Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);return 1; }
        }
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        using(var form=new InstallerForm(root,game)) {
            if(preview!="") {
                form.CreateControl();using(var bitmap=new Bitmap(form.Width,form.Height)) {
                    form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));
                    int left=(form.Width-form.ClientSize.Width)/2,top=form.Height-form.ClientSize.Height-left;
                    foreach(Control child in form.Controls)child.DrawToBitmap(bitmap,new Rectangle(child.Left+left,child.Top+top,child.Width,child.Height));
                    bitmap.Save(preview);
                }return 0;
            }
            Application.Run(form);
        }
        return 0;
    }
}
