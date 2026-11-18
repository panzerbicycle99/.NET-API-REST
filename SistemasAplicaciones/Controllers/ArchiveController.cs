using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SistemasAplicaciones.Data;
using SistemasAplicaciones.Models;
using System.Security.Cryptography;
using static SistemasAplicaciones.Models.EstructurasLineales;

namespace SistemasAplicaciones.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ArchiveController : ControllerBase
{
    private readonly AppDbContext _context;
    private static PilaHistorial historial = new PilaHistorial();
    private static ColaDescargas colaDescargas = new ColaDescargas();
    private static TablaHash tablaHash = new TablaHash();

    public ArchiveController(AppDbContext context)
    {
        _context = context;
    }

    // 📤 SUBIR archivo (almacena en BD como BYTEA)
    [HttpPost("upload")]
    [RequestSizeLimit(2147483647)]        // 2 GB
    [RequestFormLimits(MultipartBodyLengthLimit = 2147483647)]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        Check.FileUpload(file);

        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        var contenido = memoryStream.ToArray();

        var hash = MD5.HashData(contenido);
        var hashString = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

        var archivo = new Archivo
        {
            NombreOriginal = file.FileName,
            Contenido = contenido,
            HashMd5 = hashString,
            TamanoBytes = file.Length,
            TipoMime = file.ContentType ?? "application/zip"
        };

        _context.Archivos.Add(archivo);
        await _context.SaveChangesAsync();
        tablaHash.Insertar(file.FileName, archivo.Id.ToString());
        return Ok(new
        {
            message = "Archivo subido correctamente",
            id = archivo.Id,
            nombre = archivo.NombreOriginal,
            tamano = archivo.TamanoBytes,
            hash = archivo.HashMd5
        });
    }

    // 📋 LISTAR archivos (solo metadatos, sin contenido)
    [HttpGet("list")]
    public async Task<IActionResult> ListFiles()
    {
        var archivos = await _context.Archivos
            .Select(a => new {
                a.Id,
                a.NombreOriginal,
                a.TamanoBytes,
                a.HashMd5,
                a.TipoMime,
                a.FechaSubida
            })
            .ToListAsync();

        return Ok(archivos);
    }

    // 📥 DESCARGAR archivo por ID
    [HttpGet("download/{id}")]
    public async Task<IActionResult> Download(int id)
    {
        var archivo = await _context.Archivos.FindAsync(id);
        if (archivo == null)
            return NotFound("Archivo no encontrado");

        return File(archivo.Contenido, archivo.TipoMime, archivo.NombreOriginal);
    }

    // 🗑️ ELIMINAR archivo
    [HttpDelete("delete/{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var archivo = await _context.Archivos.FindAsync(id);
        if (archivo == null)
            return NotFound("Archivo no encontrado");

        tablaHash.Eliminar(archivo.NombreOriginal);
        _context.Archivos.Remove(archivo);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Archivo eliminado correctamente" });
    }

    // 🔍 OBTENER información de un archivo
    [HttpGet("info/{id}")]
    public async Task<IActionResult> GetInfo(int id)
    {
        var archivo = await _context.Archivos
            .Where(a => a.Id == id)
            .Select(a => new { a.Id, a.NombreOriginal, a.TamanoBytes, a.HashMd5, a.TipoMime, a.FechaSubida })
            .FirstOrDefaultAsync();

        if (archivo == null)
            return NotFound("Archivo no encontrado");

        return Ok(archivo);
    }

    [HttpGet("test-db")]
    public async Task<IActionResult> TestDatabase()
    {
        try
        {
            var canConnect = await _context.Database.CanConnectAsync();
            var count = await _context.Archivos.CountAsync();

            return Ok(new
            {
                connected = canConnect,
                archivosCount = count,
                message = "Conexión exitosa a Neon.tech"
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // --- Pila: Historial de acciones ---

    [HttpPost("pila/push")]
    public IActionResult PushHistorial([FromBody] AccionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Accion))
            return BadRequest("La acción no puede estar vacía.");

        historial.Push(request.Accion);
        return Ok(new { message = "Acción agregada al historial.", accion = request.Accion });
    }

    [HttpGet("pila/pop")]
    public IActionResult PopHistorial()
    {
        try
        {
            string accion = historial.Pop();
            return Ok(new { accion, message = "Acción extraída del historial." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("pila/peek")]
    public IActionResult PeekHistorial()
    {
        try
        {
            string accion = historial.Peek();
            return Ok(new { accion });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
    // --- Cola: Gestión de descargas ---

    [HttpPost("cola/enqueue")]
    public IActionResult EnqueueDescarga([FromBody] DescargaRequest request)
    {
        // Validar que el archivo existe en la BD (opcional pero recomendado)
        var archivoExiste = _context.Archivos.Any(a => a.Id == request.IdArchivo);
        if (!archivoExiste)
            return NotFound(new { error = $"No se encontró un archivo con ID {request.IdArchivo}" });

        colaDescargas.Enqueue(request.IdArchivo);
        return Ok(new { message = $"Archivo {request.IdArchivo} encolado para descarga." });
    }

    [HttpGet("cola/dequeue")]
    public IActionResult DequeueDescarga()
    {
        try
        {
            int id = colaDescargas.Dequeue();
            return Ok(new { idArchivo = id, message = "Elemento desencolado." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("cola/peek")]
    public IActionResult PeekDescarga()
    {
        try
        {
            int id = colaDescargas.Peek();
            return Ok(new { idArchivo = id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
    // --- Tabla Hash (diccionario clave-valor) ---

    [HttpPost("hash/insertar")]
    public IActionResult InsertarHash([FromBody] HashEntryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Clave) || string.IsNullOrWhiteSpace(request.Valor))
            return BadRequest("La clave y el valor son obligatorios.");

        tablaHash.Insertar(request.Clave, request.Valor);
        return Ok(new { message = "Entrada insertada/actualizada.", clave = request.Clave, valor = request.Valor });
    }

    [HttpGet("hash/buscar")]
    public IActionResult BuscarHash([FromQuery] string clave)
    {
        string valor = tablaHash.Buscar(clave);
        if (valor == null)
            return NotFound(new { error = $"No se encontró la clave '{clave}'." });

        return Ok(new { clave, valor });
    }

    [HttpDelete("hash/eliminar")]
    public IActionResult EliminarHash([FromQuery] string clave)
    {
        bool eliminado = tablaHash.Eliminar(clave);
        if (!eliminado)
            return NotFound(new { error = $"No se encontró la clave '{clave}' para eliminar." });

        return Ok(new { message = $"Clave '{clave}' eliminada." });
    }
}

// Clase auxiliar para validaciones (sin cambios)
public static class Check
{
    public static void FileUpload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            throw new ArgumentException("No se recibió ningún archivo.");

        var extension = Path.GetExtension(file.FileName).ToLower();
        if (extension != ".zip" && extension != ".rar")
            throw new ArgumentException("Solo se permiten archivos .zip o .rar");

        if (file.Length > 100 * 1024 * 1024) // 100 MB
            throw new ArgumentException("El archivo no puede superar los 100 MB");
    }
}