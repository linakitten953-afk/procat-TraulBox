// подключаем нужные библиотеки
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

var root = H.FindRoot();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = root,
    WebRootPath = Path.Combine(root, "wwwroot"),
    Args = args
});

// сайт будет открываться по этому адресу
builder.WebHost.UseUrls("http://localhost:5090");
var app = builder.Build();

// создаем базу и заполняем ее, если она пустая
Db.DbFile = Path.Combine(root, "coffee.db");
Db.Init();

// отдаем html css js из папки wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

// короткая функция для ответа с ошибкой
IResult Err(string message, int code = 400) 
{
    return Results.Json(new { error = message }, statusCode: code);
}

// достаем пользователя по токену из заголовка
Dictionary<string, object?>? Auth(HttpRequest r)
{
    var h = r.Headers["Authorization"].ToString();
    if (!h.StartsWith("Bearer ")) 
    {
        return null;
    }
    
    string token = h.Substring(7);
    var list = Db.Q("SELECT u.id, u.name, u.email, u.role FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.token = @token", new { token });
    
    if (list.Count == 0) 
    {
        return null;
    }
    return list[0];
}

// проверяем что пользователь вошел и у него подходящая роль
IResult WithRole(HttpRequest r, string[] roles, Func<Dictionary<string, object?>, IResult> act)
{
    var u = Auth(r);
    if (u == null) 
    {
        return Err("Нужно войти в аккаунт", 401);
    }
    
    string userRole = u.Str("role");
    if (roles.Length > 0) 
    {
        bool hasRole = false;
        foreach (var role in roles) 
        {
            if (role == userRole) 
            {
                hasRole = true;
                break;
            }
        }
        if (!hasRole) 
        {
            return Err("Недостаточно прав", 403);
        }
    }
    return act(u);
}

// создаем сессию (токен) после входа или регистрации
IResult StartSession(string email)
{
    var list = Db.Q("SELECT id, name, email, role FROM users WHERE email = @email", new { email });
    var u = list[0];
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    
    Db.Exec("INSERT INTO sessions(token, user_id, created) VALUES(@token, @uid, @c)", new { token, uid = u.Int("id"), c = H.Now() });
    return Results.Json(new { token, user = u });
}

// регистрация нового клиента
app.MapPost("/api/register", (RegisterDto d) =>
{
    string name = (d.Name ?? "").Trim();
    string email = (d.Email ?? "").Trim().ToLowerInvariant();
    string pw = d.Password ?? "";
    
    if (name.Length < 2) 
    {
        return Err("Введите имя, минимум 2 буквы");
    }
    if (!H.ValidEmail(email)) 
    {
        return Err("Некорректный email");
    }
    if (pw.Length < 6) 
    {
        return Err("Пароль минимум 6 символов");
    }
    
    long count = Db.Scalar("SELECT COUNT(*) FROM users WHERE email = @email", new { email });
    if (count > 0) 
    {
        return Err("Этот email уже занят");
    }
    
    Db.Exec("INSERT INTO users(name, email, pass_hash, role, created) VALUES(@name, @email, @h, 'client', @c)", new { name, email, h = H.Hash(pw), c = H.Now() });
    return StartSession(email);
});

// вход по email и паролю
app.MapPost("/api/login", (LoginDto d) =>
{
    string email = (d.Email ?? "").Trim().ToLowerInvariant();
    var list = Db.Q("SELECT pass_hash FROM users WHERE email = @email", new { email });
    
    if (list.Count == 0) 
    {
        return Err("Неверный email или пароль");
    }
    
    var u = list[0];
    if (!H.Verify(d.Password ?? "", u.Str("pass_hash"))) 
    {
        return Err("Неверный email или пароль");
    }
    return StartSession(email);
});

// выход, просто удаляем токен
app.MapPost("/api/logout", (HttpRequest r) =>
{
    var h = r.Headers["Authorization"].ToString();
    if (h.StartsWith("Bearer ")) 
    {
        string token = h.Substring(7);
        Db.Exec("DELETE FROM sessions WHERE token = @t", new { t = token });
    }
    return Results.Json(new { ok = true });
});

// кто я такой (нужно чтобы после обновления страницы остаться в аккаунте)
app.MapGet("/api/me", (HttpRequest r) => WithRole(r, H.Any, u => Results.Json(u)));

// категории товаров
app.MapGet("/api/categories", () => 
{
    var list = Db.Q("SELECT id, name FROM categories ORDER BY id");
    return Results.Json(list);
});

// список стикеров для drag and drop
app.MapGet("/api/stickers", () => Results.Json(H.Stickers));

// каталог, показываем только товары которые есть в наличии
app.MapGet("/api/products", () => 
{
    var list = Db.Q(@"SELECT p.*, c.name AS category_name FROM products p
                      JOIN categories c ON c.id = p.category_id
                      WHERE p.available = 1 ORDER BY p.category_id, p.id");
    return Results.Json(list);
});

// пересчет корзины на сервере, чтобы цены брались из базы а не из браузера
app.MapPost("/api/cart/calc", (CartDto d) =>
{
    var lines = new List<Dictionary<string, object?>>();
    double total = 0;
    var itemsList = d.Items ?? new List<CartItemDto>();
    
    foreach (var it in itemsList)
    {
        var pList = Db.Q("SELECT p.*, c.name AS category_name FROM products p JOIN categories c ON c.id = p.category_id WHERE p.id = @id", new { id = it.ProductId });
        var p = pList.Count > 0 ? pList[0] : null;
        int qty = Math.Max(1, Math.Min(20, it.Qty));
        bool ok = p != null && p.Int("available") == 1;
        
        var line = new Dictionary<string, object?> { { "productId", it.ProductId }, { "ok", ok }, { "qty", qty } };
        if (p != null)
        {
            line["name"] = p.Str("name");
            line["price"] = p.Dbl("price");
            line["image_url"] = p.Str("image_url");
            line["category_name"] = p.Str("category_name");
            line["sum"] = Math.Round(p.Dbl("price") * qty, 2);
            if (ok) 
            {
                total += p.Dbl("price") * qty;
            }
        }
        lines.Add(line);
    }
    return Results.Json(new { lines, total = Math.Round(total, 2) });
});

// оформление заказа
app.MapPost("/api/orders", (HttpRequest r, OrderDto d) => WithRole(r, H.Any, u =>
{
    if (d.Items == null || d.Items.Count == 0) 
    {
        return Err("Корзина пустая");
    }
    if (d.Items.Count > 30) 
    {
        return Err("Слишком много позиций в заказе");
    }
    
    string payment = d.Payment ?? "";
    string delivery = d.Delivery ?? "";
    
    if (!H.Payments.Contains(payment)) 
    {
        return Err("Выберите способ оплаты");
    }
    if (!H.Deliveries.Contains(delivery)) 
    {
        return Err("Выберите способ получения");
    }
    
    string address = (d.Address ?? "").Trim();
    if (delivery == "delivery" && address.Length < 5) 
    {
        return Err("Укажите адрес доставки");
    }
    if (address.Length > 200) 
    {
        return Err("Адрес слишком длинный");
    }
    
    string comment = (d.Comment ?? "").Trim();
    if (comment.Length > 300) 
    {
        return Err("Комментарий слишком длинный");
    }

    // проверяем каждую позицию и считаем сумму
    var lines = new List<(Dictionary<string, object?> p, int qty, string st)>();
    double total = 0;
    foreach (var it in d.Items)
    {
        if (it.Qty < 1 || it.Qty >100) 
        {
            return Err("Количество должно быть от 1 до 100");
        }
        
        var pList = Db.Q("SELECT p.*, c.name AS category_name FROM products p JOIN categories c ON c.id = p.category_id WHERE p.id = @id", new { id = it.ProductId });
        var p = pList.Count > 0 ? pList[0] : null;
        
        if (p == null || p.Int("available") == 0) 
        {
            return Err("Один из товаров уже недоступен, обновите корзину");
        }
        
        string st = H.CleanStickers(it.Stickers);
        if (st == null) 
        {
            return Err("Неизвестный стикер");
        }
        
        total += p.Dbl("price") * it.Qty;
        lines.Add((p, it.Qty, st));
    }
    total = Math.Round(total, 2);

    // сохраняем заказ и позиции
    long orderId = Db.Insert(@"INSERT INTO orders(user_id, total, payment, delivery, address, comment, status, created)
                               VALUES(@uid, @total, @pay, @del, @addr, @com, 'new', @c)",
        new { uid = u.Int("id"), total, pay = payment, del = delivery, addr = address, com = comment, c = H.Now() });
        
    foreach (var l in lines)
    {
        Db.Exec(@"INSERT INTO order_items(order_id, product_id, name, category, price, qty, stickers)
                  VALUES(@oid, @pid, @n, @cat, @pr, @q, @st)",
            new { oid = orderId, pid = l.p.Int("id"), n = l.p.Str("name"), cat = l.p.Str("category_name"), pr = l.p.Dbl("price"), q = l.qty, st = l.st });
    }
    return Results.Json(new { id = orderId });
}));

// мои заказы для профиля
app.MapGet("/api/orders/my", (HttpRequest r) => WithRole(r, H.Any, u =>
{
    var list = Db.Q("SELECT * FROM orders WHERE user_id = @uid ORDER BY id DESC", new { uid = u.Int("id") });
    return Results.Json(list);
}));

// один заказ со всеми позициями, из него делаем чек
app.MapGet("/api/orders/{id:long}", (long id, HttpRequest r) => WithRole(r, H.Any, u =>
{
    var list = Db.Q("SELECT o.*, u.name AS user_name, u.email AS user_email FROM orders o JOIN users u ON u.id = o.user_id WHERE o.id = @id", new { id });
    if (list.Count == 0) 
    {
        return Err("Заказ не найден", 404);
    }
    
    var o = list[0];
    if (u.Str("role") != "admin" && o.Int("user_id") != u.Int("id")) 
    {
        return Err("Это чужой заказ", 403);
    }
    
    var items = Db.Q("SELECT name, category, price, qty, stickers FROM order_items WHERE order_id = @id ORDER BY id", new { id });
    return Results.Json(new { order = o, items });
}));

// блог для всех
app.MapGet("/api/posts", () => 
{
    var list = Db.Q("SELECT id, title, summary, image_url, created FROM posts ORDER BY id DESC");
    return Results.Json(list);
});

app.MapGet("/api/posts/{id:long}", (long id) =>
{
    var list = Db.Q("SELECT * FROM posts WHERE id = @id", new { id });
    if (list.Count == 0) 
    {
        return Err("Статья не найдена", 404);
    }
    return Results.Json(list[0]);
});

// проверка полей товара (общая для добавления и редактирования)
string? CheckProduct(ProductDto d)
{
    if ((d.Name ?? "").Trim().Length < 2) 
    {
        return "Название минимум 2 символа";
    }
    if (Db.Scalar("SELECT COUNT(*) FROM categories WHERE id = @id", new { id = d.CategoryId }) == 0) 
    {
        return "Выберите категорию";
    }
    if (d.Price <= 0 || d.Price > 100000) 
    {
        return "Цена должна быть больше 0";
    }
    if ((d.Description ?? "").Length > 400) 
    {
        return "Описание слишком длинное";
    }
    if ((d.ImageUrl ?? "").Length > 1000) 
    {
        return "Ссылка на картинку слишком длинная";
    }
    return null;
}

// админка товаров
app.MapGet("/api/admin/products", (HttpRequest r) => WithRole(r, H.AdminOnly, u =>
{
    var list = Db.Q("SELECT p.*, c.name AS category_name FROM products p JOIN categories c ON c.id = p.category_id ORDER BY p.id");
    return Results.Json(list);
}));

app.MapPost("/api/admin/products", (HttpRequest r, ProductDto d) => WithRole(r, H.AdminOnly, u =>
{
    string msg = CheckProduct(d);
    if (msg != null) 
    {
        return Err(msg);
    }
    
    Db.Exec(@"INSERT INTO products(name, category_id, description, image_url, price, available)
              VALUES(@n, @c, @ds, @im, @p, @av)",
        new { n = d.Name.Trim(), c = d.CategoryId, ds = (d.Description ?? "").Trim(), im = (d.ImageUrl ?? "").Trim(), p = d.Price, av = d.Available == 0 ? 0 : 1 });
    return Results.Json(new { ok = true });
}));

app.MapPut("/api/admin/products/{id:long}", (long id, HttpRequest r, ProductDto d) => WithRole(r, H.AdminOnly, u =>
{
    string msg = CheckProduct(d);
    if (msg != null) 
    {
        return Err(msg);
    }
    
    long count = Db.Scalar("SELECT COUNT(*) FROM products WHERE id = @id", new { id });
    if (count == 0) 
    {
        return Err("Товар не найден", 404);
    }
    
    Db.Exec(@"UPDATE products SET name = @n, category_id = @c, description = @ds, image_url = @im, price = @p, available = @av WHERE id = @id",
        new { id, n = d.Name.Trim(), c = d.CategoryId, ds = (d.Description ?? "").Trim(), im = (d.ImageUrl ?? "").Trim(), p = d.Price, av = d.Available == 0 ? 0 : 1 });
    return Results.Json(new { ok = true });
}));

app.MapDelete("/api/admin/products/{id:long}", (long id, HttpRequest r) => WithRole(r, H.AdminOnly, u =>
{
    // в старых заказах название товара хранится отдельно, поэтому удалять можно
    Db.Exec("DELETE FROM products WHERE id = @id", new { id });
    return Results.Json(new { ok = true });
}));

// админка блога
string? CheckPost(PostDto d)
{
    if ((d.Title ?? "").Trim().Length < 3) 
    {
        return "Заголовок минимум 3 символа";
    }
    if ((d.Title ?? "").Length > 120) 
    {
        return "Заголовок слишком длинный";
    }
    if ((d.Summary ?? "").Trim().Length < 5) 
    {
        return "Напишите краткое описание";
    }
    if ((d.Summary ?? "").Length > 300) 
    {
        return "Краткое описание слишком длинное";
    }
    if ((d.Body ?? "").Trim().Length < 20) 
    {
        return "Текст статьи слишком короткий";
    }
    if ((d.Body ?? "").Length > 10000) 
    {
        return "Текст статьи слишком длинный";
    }
    return null;
}

app.MapPost("/api/admin/posts", (HttpRequest r, PostDto d) => WithRole(r, H.AdminOnly, u =>
{
    string msg = CheckPost(d);
    if (msg != null) 
    {
        return Err(msg);
    }
    
    Db.Exec("INSERT INTO posts(title, summary, body, image_url, created) VALUES(@t, @s, @b, @i, @c)",
        new { t = d.Title.Trim(), s = d.Summary.Trim(), b = d.Body.Trim(), i = (d.ImageUrl ?? "").Trim(), c = H.Now() });
    return Results.Json(new { ok = true });
}));

app.MapPut("/api/admin/posts/{id:long}", (long id, HttpRequest r, PostDto d) => WithRole(r, H.AdminOnly, u =>
{
    string msg = CheckPost(d);
    if (msg != null) 
    {
        return Err(msg);
    }
    
    long count = Db.Scalar("SELECT COUNT(*) FROM posts WHERE id = @id", new { id });
    if (count == 0) 
    {
        return Err("Статья не найдена", 404);
    }
    
    Db.Exec("UPDATE posts SET title = @t, summary = @s, body = @b, image_url = @i WHERE id = @id",
        new { id, t = d.Title.Trim(), s = d.Summary.Trim(), b = d.Body.Trim(), i = (d.ImageUrl ?? "").Trim() });
    return Results.Json(new { ok = true });
}));

app.MapDelete("/api/admin/posts/{id:long}", (long id, HttpRequest r) => WithRole(r, H.AdminOnly, u =>
{
    Db.Exec("DELETE FROM posts WHERE id = @id", new { id });
    return Results.Json(new { ok = true });
}));

// все заказы для админа
app.MapGet("/api/admin/orders", (HttpRequest r) => WithRole(r, H.AdminOnly, u =>
{
    var list = Db.Q(@"SELECT o.*, u.name AS user_name, u.email AS user_email,
                      (SELECT COUNT(*) FROM order_items WHERE order_id = o.id) AS positions
                      FROM orders o JOIN users u ON u.id = o.user_id ORDER BY o.id DESC LIMIT 300");
    return Results.Json(list);
}));

// смена статуса заказа
app.MapPut("/api/admin/orders/{id:long}/status", (long id, HttpRequest r, StatusDto d) => WithRole(r, H.AdminOnly, u =>
{
    string st = d.Status ?? "";
    if (!H.Statuses.Contains(st)) 
    {
        return Err("Неизвестный статус");
    }
    
    long count = Db.Scalar("SELECT COUNT(*) FROM orders WHERE id = @id", new { id });
    if (count == 0) 
    {
        return Err("Заказ не найден", 404);
    }
    
    Db.Exec("UPDATE orders SET status = @st WHERE id = @id", new { id, st });
    return Results.Json(new { ok = true });
}));

// статистика и аналитика
app.MapGet("/api/admin/stats", (HttpRequest r) => WithRole(r, H.AdminOnly, u =>
{
    long orders = Db.Scalar("SELECT COUNT(*) FROM orders WHERE status != 'cancelled'");
    double revenue = Db.Num("SELECT COALESCE(SUM(total), 0) FROM orders WHERE status != 'cancelled'");
    long clients = Db.Scalar("SELECT COUNT(*) FROM users WHERE role = 'client'");
    long today = Db.Scalar("SELECT COUNT(*) FROM orders WHERE status != 'cancelled' AND substr(created, 1, 10) = @d", new { d = H.Fmt(DateTime.Today) });
    double avg = orders == 0 ? 0 : Math.Round(revenue / orders, 2);

    // выручка по дням за последние 14 дней
    string since = H.Fmt(DateTime.Today.AddDays(-13));
    var found = Db.Q(@"SELECT substr(created, 1, 10) AS day, COUNT(*) AS cnt, SUM(total) AS sum
                       FROM orders WHERE status != 'cancelled' AND substr(created, 1, 10) >= @since GROUP BY day", new { since });
    
    var days = new List<Dictionary<string, object?>>();
    for (int i = 13; i >= 0; i--)
    {
        string ds = H.Fmt(DateTime.Today.AddDays(-i));
        Dictionary<string, object?>? row = null;
        foreach (var f in found)
        {
            if (f.Str("day") == ds)
            {
                row = f;
                break;
            }
        }
        
        var dayData = new Dictionary<string, object?>();
        dayData["day"] = ds;
        dayData["cnt"] = row == null ? 0 : row.Int("cnt");
        dayData["sum"] = row == null ? 0 : row.Dbl("sum");
        days.Add(dayData);
    }

    // по категориям
    var cats = Db.Q(@"SELECT oi.category AS name, SUM(oi.qty) AS cnt, SUM(oi.price * oi.qty) AS sum
                      FROM order_items oi JOIN orders o ON o.id = oi.order_id
                      WHERE o.status != 'cancelled' GROUP BY oi.category ORDER BY sum DESC");

    // самые популярные товары
    var top = Db.Q(@"SELECT oi.name, SUM(oi.qty) AS cnt
                     FROM order_items oi JOIN orders o ON o.id = oi.order_id
                     WHERE o.status != 'cancelled' GROUP BY oi.name ORDER BY cnt DESC, oi.name LIMIT 6");

    // какие стикеры чаще всего ставят
    var stickers = new List<Dictionary<string, object?>>();
    foreach (var s in H.Stickers)
    {
        long c = Db.Scalar(@"SELECT COUNT(*) FROM order_items oi JOIN orders o ON o.id = oi.order_id
                            WHERE o.status != 'cancelled' AND (',' || oi.stickers || ',') LIKE @pat", new { pat = "%," + s + ",%" });
        var stData = new Dictionary<string, object?>();
        stData["name"] = s;
        stData["cnt"] = c;
        stickers.Add(stData);
    }

    // последние заказы для таблицы
    var recent = Db.Q(@"SELECT o.id, o.total, o.status, o.created, u.name AS user_name
                        FROM orders o JOIN users u ON u.id = o.user_id ORDER BY o.id DESC LIMIT 8");
    return Results.Json(new { orders, revenue, clients, today, avg, days, cats, top, stickers, recent });
}));

// открываем браузер сами, когда сервер запустился
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        string url = "http://localhost:5090";
        if (OperatingSystem.IsWindows()) 
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS()) 
        {
            Process.Start("open", url);
        }
        else 
        {
            Process.Start("xdg-open", url);
        }
    }
    catch
    {
    }
});

app.Run();

// модели данных которые приходят с сайта
record RegisterDto(string? Name, string? Email, string? Password);
record LoginDto(string? Email, string? Password);
record CartItemDto(long ProductId, int Qty, List<string>? Stickers);
record CartDto(List<CartItemDto>? Items);
record OrderDto(List<CartItemDto>? Items, string? Payment, string? Delivery, string? Address, string? Comment);
record ProductDto(string? Name, long CategoryId, string? Description, string? ImageUrl, double Price, int Available);
record PostDto(string? Title, string? Summary, string? Body, string? ImageUrl);
record StatusDto(string? Status);

// маленькие помощники чтобы короче доставать значения из словаря
static class X
{
    public static string Str(this Dictionary<string, object?> d, string k) 
    {
        var v = d[k];
        if (v == null) return "";
        return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
    }
    
    public static long Int(this Dictionary<string, object?> d, string k) 
    {
        var v = d[k];
        if (v == null) return 0;
        return Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }
    
    public static double Dbl(this Dictionary<string, object?> d, string k) 
    {
        var v = d[k];
        if (v == null) return 0;
        return Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }
}

// разные общие функции и списки
static class H
{
    public static readonly string[] Any = Array.Empty<string>();
    public static readonly string[] AdminOnly = new[] { "admin" };

    // стикеры которые можно перетащить на товар
    public static readonly string[] Stickers = new[] { "Срочно", "С собой", "Подарок", "Без сахара", "Побольше сахара" };
    public static readonly string[] Payments = new[] { "card", "cash", "online" };
    public static readonly string[] Deliveries = new[] { "pickup", "delivery" };
    public static readonly string[] Statuses = new[] { "new", "cooking", "ready", "done", "cancelled" };

    // ищем папку где лежит csproj и wwwroot
    public static string FindRoot()
    {
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            var d = new DirectoryInfo(start);
            while (d != null)
            {
                string path1 = Path.Combine(d.FullName, "wwwroot");
                string path2 = Path.Combine(d.FullName, "CoffeeApp.csproj");
                
                if (Directory.Exists(path1) && File.Exists(path2))
                {
                    return d.FullName;
                }
                d = d.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    public static string Now()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
    
    public static string Fmt(DateTime d)
    {
        return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // простая проверка email
    public static bool ValidEmail(string e)
    {
        if (e.Length < 5 || e.Length > 100) 
        {
            return false;
        }
        
        int at = e.IndexOf('@');
        if (at <= 0) 
        {
            return false;
        }
        if (at != e.LastIndexOf('@')) 
        {
            return false;
        }
        if (e.IndexOf('.', at) <= at + 1) 
        {
            return false;
        }
        if (e.EndsWith(".") || e.Contains(' ')) 
        {
            return false;
        }
        return true;
    }

    // пароль хранится не в открытом виде, а как хеш с солью
    public static string Hash(string pw)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(key);
    }

    public static bool Verify(string pw, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) 
        {
            return false;
        }
        
        var salt = Convert.FromBase64String(parts[0]);
        var key = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100000, HashAlgorithmName.SHA256, 32);
        var expected = Convert.FromBase64String(parts[1]);
        
        return CryptographicOperations.FixedTimeEquals(key, expected);
    }

    // проверяем список стикеров и склеиваем через запятую, если что-то не так вернем null
    public static string? CleanStickers(List<string>? list)
    {
        if (list == null || list.Count == 0) 
        {
            return "";
        }
        
        var clean = new List<string>();
        foreach (var s in list)
        {
            if (!clean.Contains(s))
            {
                clean.Add(s);
            }
        }
        
        if (clean.Count > 5) 
        {
            return null;
        }
        
        foreach (var s in clean)
        {
            bool found = false;
            foreach (var valid in Stickers)
            {
                if (valid == s) 
                {
                    found = true;
                    break;
                }
            }
            if (!found) 
            {
                return null;
            }
        }
        
        return string.Join(",", clean);
    }
}

// класс для работы с базой
static class Db
{
    public static string DbFile = "";

    static SqliteConnection Open()
    {
        var c = new SqliteConnection("Data Source=" + DbFile);
        c.Open();
        using var m = c.CreateCommand();
        m.CommandText = "PRAGMA foreign_keys = ON;";
        m.ExecuteNonQuery();
        return c;
    }

    // кладем параметры из анонимного объекта в запрос
    static void Bind(SqliteCommand m, object? p)
    {
        if (p == null) return;
        foreach (var pr in p.GetType().GetProperties())
        {
            var val = pr.GetValue(p);
            if (val == null) 
            {
                val = DBNull.Value;
            }
            m.Parameters.AddWithValue("@" + pr.Name, val);
        }
    }

    // выбрать много строк
    public static List<Dictionary<string, object?>> Q(string sql, object? p = null)
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = sql;
        Bind(m, p);
        using var r = m.ExecuteReader();
        var list = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++) 
            {
                if (r.IsDBNull(i)) 
                {
                    row[r.GetName(i)] = null;
                }
                else 
                {
                    row[r.GetName(i)] = r.GetValue(i);
                }
            }
            list.Add(row);
        }
        return list;
    }

    // выполнить запрос без результата
    public static int Exec(string sql, object? p = null)
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = sql;
        Bind(m, p);
        return m.ExecuteNonQuery();
    }

    // вставить строку и узнать ее id
    public static long Insert(string sql, object? p = null)
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = sql;
        Bind(m, p);
        m.ExecuteNonQuery();
        using var m2 = c.CreateCommand();
        m2.CommandText = "SELECT last_insert_rowid()";
        var res = m2.ExecuteScalar();
        return Convert.ToInt64(res, CultureInfo.InvariantCulture);
    }

    // одно целое число
    public static long Scalar(string sql, object? p = null)
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = sql;
        Bind(m, p);
        var o = m.ExecuteScalar();
        if (o == null || o is DBNull) 
        {
            return 0;
        }
        return Convert.ToInt64(o, CultureInfo.InvariantCulture);
    }

    // одно дробное число
    public static double Num(string sql, object? p = null)
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = sql;
        Bind(m, p);
        var o = m.ExecuteScalar();
        if (o == null || o is DBNull) 
        {
            return 0;
        }
        return Convert.ToDouble(o, CultureInfo.InvariantCulture);
    }

    // создаем таблицы если их еще нет
    public static void Init()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS users(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  email TEXT NOT NULL UNIQUE,
  pass_hash TEXT NOT NULL,
  role TEXT NOT NULL CHECK(role IN ('client', 'admin')),
  created TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS sessions(
  token TEXT PRIMARY KEY,
  user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  created TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS categories(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL UNIQUE
);
CREATE TABLE IF NOT EXISTS products(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  category_id INTEGER NOT NULL REFERENCES categories(id),
  description TEXT NOT NULL DEFAULT '',
  image_url TEXT NOT NULL DEFAULT '',
  price REAL NOT NULL CHECK(price > 0),
  available INTEGER NOT NULL DEFAULT 1 CHECK(available IN (0, 1))
);
CREATE TABLE IF NOT EXISTS orders(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id INTEGER NOT NULL REFERENCES users(id),
  total REAL NOT NULL CHECK(total >= 0),
  payment TEXT NOT NULL CHECK(payment IN ('card', 'cash', 'online')),
  delivery TEXT NOT NULL CHECK(delivery IN ('pickup', 'delivery')),
  address TEXT NOT NULL DEFAULT '',
  comment TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'new' CHECK(status IN ('new', 'cooking', 'ready', 'done', 'cancelled')),
  created TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS order_items(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  order_id INTEGER NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
  product_id INTEGER REFERENCES products(id) ON DELETE SET NULL,
  name TEXT NOT NULL,
  category TEXT NOT NULL,
  price REAL NOT NULL,
  qty INTEGER NOT NULL CHECK(qty > 0),
  stickers TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS posts(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  title TEXT NOT NULL,
  summary TEXT NOT NULL,
   body TEXT NOT NULL,
  image_url TEXT NOT NULL DEFAULT '',
  created TEXT NOT NULL
);");
        if (Scalar("SELECT COUNT(*) FROM users") == 0) Seed();
    }

    // начальные данные: пользователи, меню, статьи и заказы
    static void Seed()
    {
        var now = H.Now();
        
        // Админ (пароль admin123)
        Exec("INSERT INTO users(name, email, pass_hash, role, created) VALUES('Администратор', 'admin@mail.ru', @h, 'admin', @c)", new { h = H.Hash("admin123"), c = now });
        // Клиент (пароль client123)
        Exec("INSERT INTO users(name, email, pass_hash, role, created) VALUES('Елизабет Робертсон', 'client@mail.ru', @h, 'client', @c)", new { h = H.Hash("client123"), c = now });
        
        Exec("INSERT INTO categories(name) VALUES('Кофе'), ('Десерты'), ('Выпечка')");

        var items = new (string Name, int Cat, string Desc, string Img, double Price, int Av)[]
        {
            ("Эспрессо", 1, "Крепкий насыщенный кофе на двойной порции зерен", "https://avatars.mds.yandex.net/i?id=38cd06ff81c16a14e3e07cf1baab5d93_l-9234614-images-thumbs&n=13", 150, 1),
            ("Капучино", 1, "Эспрессо с нежной молочной пенкой", "https://img.povar.ru/uploads/0c/c4/5b/e4/kapuchino_s_koricei-841425.jpg", 220, 1),
            ("Латте", 1, "Много молока и мягкий кофейный вкус", "https://poetti.ru/upload/medialibrary/dd8/6wk4bp92nj44vq036wp378rtdk6bm20v/04-poetti-seo-latte-1000x650.jpg", 240, 1),
            ("Айс латте", 1, "Холодный кофе со льдом, идеален в жару", "https://avatars.mds.yandex.net/i?id=848113027a8d910ddabdac99d286433046bbfbe0-16400625-images-thumbs&n=13", 260, 1),
            ("Чизкейк", 2, "Классический сливочный чизкейк на песочной основе", "https://images.news.ru/2025/11/25/7pXWDoFoAajygGkSkA3b88MHDKmc7V2YTVR4k18V_780.png", 290, 1),
            ("Тирамису", 2, "Нежный десерт с маскарпоне и кофейной пропиткой", "https://cdn.food.ru/unsigned/fit/640/480/ce/0/czM6Ly9tZWRpYS9waWN0dXJlcy8yMDI1MDcyNS95QWNxczYuanBlZw.webp", 310, 1),
            ("Макаронс", 2, "Набор из трех французских пирожных", "https://images.news.ru/2025/08/22/XbDK44HqTiNfEjT8iCvcEHxPSW2mZ0aa5Diq4VIP_780.png", 270, 1),
            ("Брауни", 2, "Шоколадный брауни с хрустящей корочкой", "https://i.pinimg.com/736x/6f/03/3b/6f033ba3f988348192bc7fef1c97feaa.jpg", 180, 1),
            ("Круассан с шоколадом", 3, "Слоеный круассан, выпекается каждое утро", "https://images.gastronom.ru/M12WP7d8js-q6hRbd_hlsx5-duSfB2OrxqiD8Hq503U/pr:recipe-cover-image/g:ce/rs:auto:0:0:0/L2Ntcy9hbGwtaW1hZ2VzL2U3ZGUzNzAxLWJmOWQtNDBiMC04M2VhLTI5NDExODI1ZGRiNS5qcGc.webp", 140, 1),
            ("Булочка с корицей", 3, "Мягкая булочка с корицей и сливочным кремом", "https://avatars.mds.yandex.net/i?id=3e298603c822f80dbc710cde57ea6c41_l-5491132-images-thumbs&n=13", 170, 1),
            ("Маффин с черникой", 3, "Пышный маффин с ягодами внутри", "https://avatars.mds.yandex.net/i?id=823788c1e16ca2dfe432602ee7a7b48b_l-10851151-images-thumbs&n=13", 160, 1),
            ("Сэндвич с индейкой", 3, "Теплая чиабатта с индейкой, сыром и свежими овощами", "https://img.povar.ru/main-micro/51/a8/b6/2b/sendvich_s_indeikoi-895930.JPG", 280, 1)
        };
        
        foreach (var i in items)
        {
            Exec(@"INSERT INTO products(name, category_id, description, image_url, price, available) VALUES(@n, @c, @d, @i, @p, @a)",
                new { n = i.Name, c = i.Cat, d = i.Desc, i = i.Img, p = i.Price, a = i.Av });
        }

        // статьи для блога
        var posts = new (string T, string S, string B, string I)[]
        {
            ("Чем арабика отличается от либерики", "Два главных вида кофе и почему они такие разные на вкус", "Арабика растет высоко в горах, у нее мягкий вкус с кислинкой и сладкими нотами. Либерика растет ниже, она крепче и горчит сильнее, а еще в ней больше кофеина.\n\nВ нашей кофейне основной кофе сделан из арабики, а для эспрессо мы добавляем немного либерики. Так получается плотная пенка и насыщенный вкус.\n\nПопробуйте сравнить два напитка и выберите свой любимый.", "https://i.pinimg.com/originals/6c/fa/a8/6cfaa8af3beb881c1420480f93a14a6d.jpg?nii=t"),
            ("Как правильно заваривать кофе дома", "Простые советы, чтобы напиток получался как в кофейне", "Первое правило это свежие зерна, мелите их прямо перед заваркой. Второе правило это вода, она не должна быть кипятком, лучше около 92 градусов.\n\nТретье правило это пропорции. Для фильтра берите примерно 60 граммов кофе на литр воды. Для турки хватит чайной ложки на небольшую чашку.\n\nИ главное не бойтесь экспериментировать с помолом и временем заваривания.", "https://www.torrefacto.ru/upload/uf/387/wqz8rxpah0kh61cxpw0apxeggdgxca94.jpg"),
            ("Что такое латте арт", "Рассказываем, как рисуют на молочной пенке", "Латте арт это узоры на поверхности кофе. Для него нужна плотная и блестящая молочная пенка, а еще чуть-чуть практики.\n\nСамые простые рисунки это сердечко и тюльпан. Бариста наливает молоко тонкой струйкой, а потом двигает кувшин, и появляется рисунок.\n\nЕсли вы хотите красивую пенку, просите наших бариста сделать для вас рисунок.", "https://www.coffeemag.ru/himages/postimg/images/latte-art.jpg"),
            ("Сочетаем кофе и десерты", "Какой напиток подойдет к чизкейку, а какой к брауни", "К сладким десертам лучше брать несладкий кофе. Эспрессо отлично уравновешивает вкус чизкейка и тирамису.\n\nК шоколадному брауни подойдет капучино, его молочная нота смягчает шоколад. А к круассану с шоколадом хорошо идет обычный американо.\n\nЕсли не можете выбрать, спросите совета у нашего бариста.", "https://www.torrefacto.ru/upload/medialibrary/36a/40rko1wdbw6r33595nrqt3dvnajlbk1s.png")
        };
        
        var d = 0;
        foreach (var p in posts)
        {
            Exec("INSERT INTO posts(title, summary, body, image_url, created) VALUES(@t, @s, @b, @i, @c)",
                new { t = p.T, s = p.S, b = p.B, i = p.I, c = DateTime.Now.AddDays(-3 * (d++) - 1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) });
        }

        // делаем заказы за последние две недели чтобы графики не были пустыми
        var rnd = new Random(11);
        var pay = H.Payments;
        var del = H.Deliveries;
        
        for (var k = 0; k < 42; k++)
        {
            var when = DateTime.Today.AddDays(-rnd.Next(0, 14)).AddHours(8 + rnd.Next(0, 11)).AddMinutes(rnd.Next(0, 60));
            var cnt = rnd.Next(1, 4);
            var lines = new List<(int id, int qty)>();
            double total = 0;
            
            for (var j = 0; j < cnt; j++)
            {
                var pid = rnd.Next(1, 13);
                var qty = rnd.Next(1, 3);
                lines.Add((pid, qty));
                total += items[pid - 1].Price * qty;
            }
            
            var status = when.Date == DateTime.Today ? "new" : "done";
            
            var oid = Insert(@"INSERT INTO orders(user_id, total, payment, delivery, address, comment, status, created)
                               VALUES(2, @t, @p, @d, @a, '', @s, @c)",
                new { t = total, p = pay[rnd.Next(0, 3)], d = del[k % 2], a = k % 2 == 1 ? "ул. Ленина, 10, кв. 5" : "", s = status, c = when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) });
                
            foreach (var l in lines)
            {
                var it = items[l.id - 1];
                string catName = "Кофе";
                if (it.Cat == 2) 
                {
                    catName = "Десерты";
                }
                else if (it.Cat == 3) 
                {
                    catName = "Выпечка";
                }
                
                var st = "";
                if (rnd.Next(0, 4) == 0) 
                {
                    st = H.Stickers[rnd.Next(0, H.Stickers.Length)];
                }
                
                Exec("INSERT INTO order_items(order_id, product_id, name, category, price, qty, stickers) VALUES(@o, @p, @n, @c, @pr, @q, @st)",
                    new { o = oid, p = l.id, n = it.Name, c = catName, pr = it.Price, q = l.qty, st });
            }
        }
    }
}