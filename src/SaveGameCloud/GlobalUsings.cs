// WPF n'importe pas System.IO implicitement (conflit avec System.Windows.Shapes.Path).
global using Directory = System.IO.Directory;
global using File = System.IO.File;
global using Path = System.IO.Path;
