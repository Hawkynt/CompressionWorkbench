// Oracle driver over ZenKit's Vfs: lists/extracts a VDF, or packs a host directory into one.
#include <zenkit/Vfs.hh>
#include <zenkit/Stream.hh>
#include <zenkit/Misc.hh>
#include <cstdio>
#include <cstdint>
#include <fstream>
#include <string>
#include <vector>
#include <filesystem>

static uint64_t fnv1a(std::vector<std::byte> const& d) {
	uint64_t h = 14695981039346656037ULL;
	for (auto b : d) { h ^= (uint8_t) b; h *= 1099511628211ULL; }
	return h;
}

static void walk(zenkit::VfsNode const& n, std::string const& prefix, std::filesystem::path const* out) {
	for (auto const& c : n.children()) {
		std::string p = prefix.empty() ? c.name() : prefix + "/" + c.name();
		if (c.type() == zenkit::VfsNodeType::DIRECTORY) {
			std::printf("D\t%s\n", p.c_str());
			if (out) std::filesystem::create_directories(*out / p);
			walk(c, p, out);
		} else {
			auto r = c.open_read();
			std::vector<std::byte> buf;
			std::byte tmp[65536];
			for (;;) { auto got = r->read(tmp, sizeof tmp); if (got == 0) break; buf.insert(buf.end(), tmp, tmp + got); }
			std::printf("F\t%s\t%zu\t%016llx\n", p.c_str(), buf.size(), (unsigned long long) fnv1a(buf));
			if (out) {
				std::filesystem::create_directories((*out / p).parent_path());
				std::ofstream f(*out / p, std::ios::binary);
				f.write((char const*) buf.data(), (std::streamsize) buf.size());
			}
		}
	}
}

int main(int argc, char** argv) {
	zenkit::Logger::set_default(zenkit::LogLevel::ERROR);
	if (argc < 3) { std::fprintf(stderr, "usage: list <vdf> | extract <vdf> <dir> | pack <dir> <vdf> [zipped]\n"); return 2; }
	std::string cmd = argv[1];
	try {
		if (cmd == "list" || cmd == "extract") {
			zenkit::Vfs v;
			v.mount_disk(std::filesystem::path(argv[2]));
			std::filesystem::path out = argc > 3 ? argv[3] : "";
			walk(v.root(), "", cmd == "extract" ? &out : nullptr);
			return 0;
		}
		if (cmd == "pack" && argc >= 4) {
			zenkit::Vfs v;
			v.mount_host(argv[2], "/");
			std::vector<std::byte> bytes;
			{
				auto w = zenkit::Write::to(&bytes);
				bool zipped = argc > 4 && std::string(argv[4]) == "zipped";
				if (zipped) v.save_compressed(w.get(), zenkit::GameVersion::GOTHIC_2, 1600000000);
				else v.save(w.get(), zenkit::GameVersion::GOTHIC_2, 1600000000);
			}
			std::ofstream f(argv[3], std::ios::binary);
			f.write((char const*) bytes.data(), (std::streamsize) bytes.size());
			return 0;
		}
	} catch (std::exception const& e) {
		std::fprintf(stderr, "error: %s\n", e.what());
		return 1;
	}
	return 2;
}
