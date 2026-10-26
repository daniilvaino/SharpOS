// What <ImplicitUsings>enable</ImplicitUsings> gives the upstream projects,
// less System.Net.Http.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

// std compiles into this assembly, so its internal System.DateTimeFormat (the BCL's
// formatter) is visible here and collides with Okojo's own type of that name.
global using DateTimeFormat = Okojo.Globalization.DateTimeFormat;
global using CalendarData = Okojo.Globalization.CalendarData;
